﻿// Copyright (C) 2015 Tom Deseyn
// Licensed under GNU LGPL, Version 2.1. See LICENSE in the project root for license information.

using System;
using System.Collections.Generic;
using System.Net.WebSockets;
using System.Threading.Tasks;
using System.Threading;
using System.Collections.Concurrent;

namespace Tmds.SockJS
{
    class Session
    {
        private const int SendOpen = 0;
        private const int SendDisposed = -1;
        private const int SendCloseSent = -2;
        private const int SendClientTimeout = -3;

        private const int ReceiveNone = 0;
        private const int ReceiveOne = 1;
        private const int ReceiveDisposed = -1;
        private const int ReceiveCloseReceived = -2;


        public static readonly byte[] DisposeCloseBuffer;
        public static readonly byte[] SendErrorCloseBuffer;
        public static readonly PendingReceive CloseSentPendingReceive;
        public static readonly PendingReceive CloseNotSentPendingReceive;
        private const string ReceiverIsClosed = "The receiver is closed";
        private const string SimultaneousReceivesNotSupported = "Simultaneous receives are not supported";

        static Session()
        {
            DisposeCloseBuffer = Receiver.CloseBuffer(WebSocketCloseStatus.EndpointUnavailable, "Going Away");
            SendErrorCloseBuffer = Receiver.CloseBuffer(WebSocketCloseStatus.ProtocolError, "Connection interrupted");
            CloseNotSentPendingReceive = new PendingReceive(WebSocketCloseStatus.EndpointUnavailable, "Going Away");
            CloseSentPendingReceive = new PendingReceive(WebSocketCloseStatus.NormalClosure, "Normal Closure");
        }

        private SessionWebSocket _socket;
        private int _sendState;
        private int _receiveState;
        private string _sessionId;
        private SessionManager _sessionManager;
        private Receiver _receiver;
        private SockJSOptions _options;
        private ReaderWriterLockSlim _clientLock;
        private byte[] _closeMessage;
        private CancellationTokenSource _clientTimeoutCts;
        private ConcurrentQueue<PendingReceive> _receives;
        private SemaphoreSlim _receivesSem;
        private SemaphoreSlim _sendDequeueSem;
        private SemaphoreSlim _sendsSem;
        private SemaphoreSlim _sendEnqueueSem;
        private ConcurrentQueue<PendingSend> _sends;

        public string SessionId { get { return _sessionId; } }

        public Session(SessionManager sessionContainer, string sessionId, Receiver receiver, SockJSOptions options)
        {
            _clientLock = new ReaderWriterLockSlim();
            _sessionManager = sessionContainer;
            _sessionId = sessionId;
            _options = options;
            _receiver = receiver;
            _sendState = SendOpen;
            _receiveState = ReceiveNone;
            _receives = new ConcurrentQueue<PendingReceive>();
            _receivesSem = new SemaphoreSlim(0);
            _sendDequeueSem = new SemaphoreSlim(1);
            _sendsSem = new SemaphoreSlim(0);
            _sendEnqueueSem = new SemaphoreSlim(1);
            _sends = new ConcurrentQueue<PendingSend>();
        }

        public bool SetReceiver(Receiver receiver)
        {
            Receiver original = Interlocked.CompareExchange(ref _receiver, receiver, null);
            if (original != null)
            {
                return false;
            }
            else
            {
                CancelClientTimeout();
                return true;
            }
        }
        
        public async Task ClientReceiveAsync()
        {
            try
            {
                if (_receiver.IsNotOpen)
                {
                    await _receiver.Open();
                }
                while (!_receiver.IsClosed)
                {
                    if (_closeMessage != null)
                    {
                        await _receiver.Send(true, _closeMessage, CancellationToken.None);
                        return;
                    }

                    await _sendDequeueSem.WaitAsync(_receiver.Aborted);
                    List<PendingSend> messages = null;
                    try
                    {
                        PendingSend firstSend = null;
                        if (await _sendsSem.WaitAsync(_options.HeartbeatInterval))
                        {
                            _sends.TryDequeue(out firstSend);
                        }

                        if (firstSend == null) // timeout
                        {
                            // heartbeat
                            await _receiver.SendHeartBeat();
                            continue;
                        }

                        if (firstSend.Type == WebSocketMessageType.Close)
                        {
                            _sendDequeueSem.Release();
                            if (_closeMessage == null)
                            {
                                var closeMessage = new byte[firstSend.Buffer.Count];
                                Array.Copy(firstSend.Buffer.Array, firstSend.Buffer.Offset, closeMessage, 0, firstSend.Buffer.Count);
                                Interlocked.CompareExchange(ref _closeMessage, closeMessage, null);
                            }
                            await _receiver.Send(true, _closeMessage, CancellationToken.None);
                            return;
                        }
                        else // WebSocketMessageType.Text
                        {
                            messages = new List<PendingSend>();
                            messages.Add(firstSend);
                            PendingSend nextSend;
                            int length = firstSend.Buffer.Count + _receiver.BytesSent;
                            while (_sends.TryPeek(out nextSend) && nextSend.Type == WebSocketMessageType.Text)
                            {
                                await _sendsSem.WaitAsync();
                                _sends.TryDequeue(out nextSend);

                                messages.Add(nextSend);
                                length += nextSend.Buffer.Count;
                                if (length >= _options.MaxResponseLength)
                                {
                                    break;
                                }
                            }
                            _sendDequeueSem.Release();
                            await _receiver.SendMessages(messages);
                        }
                    }
                    catch (ObjectDisposedException) // _sendsSem
                    {
                        if (messages != null)
                        {
                            foreach (var message in messages)
                            {
                                message.CompleteDisposed();
                            }
                        }
                        PendingSend send;
                        while (_sends.TryDequeue(out send))
                        {
                            send.CompleteDisposed();
                        }
                        _sendDequeueSem.Release();
                        continue; // _closeMessage was set when _sendsSem was disposed
                    }
                }
            }
            catch
            {
                await HandleClientSendErrorAsync();
            }
            finally
            {
                ScheduleClientTimeout();
                _receiver = null;
            }
        }

        public void WebSocketDispose()
        {
            // no new _sends
            _sendEnqueueSem.Wait();
            // only dispose once
            if (_sendState == SendDisposed)
            {
                _sendEnqueueSem.Release();
                return;
            }
            _sendState = SendDisposed;
            _sendEnqueueSem.Release();

            // set close message
            Interlocked.CompareExchange(ref _closeMessage, DisposeCloseBuffer, null);

            // dispose sends
            _sendsSem.Dispose();
            _sendDequeueSem.Wait();
            PendingSend send;
            while (_sends.TryDequeue(out send))
            {
                send.CompleteDisposed();
            }
            _sendDequeueSem.Release();

            // stop receive
            _receiveState = ReceiveDisposed;
            _receivesSem.Dispose();
        }

        public async Task HandleClientSendErrorAsync()
        {
            // no new _sends
            await _sendEnqueueSem.WaitAsync();
            if (_sendState == SendOpen)
            {
                _sendState = SendClientTimeout;
            }
            _sendEnqueueSem.Release();

            // set close message
            Interlocked.CompareExchange(ref _closeMessage, SendErrorCloseBuffer, null);

            // dispose sends
            await _sendDequeueSem.WaitAsync();
            PendingSend send;
            while (_sends.TryDequeue(out send))
            {
                send.CompleteClientTimeout();
            }
            _sendDequeueSem.Release();

            // stop receive
            _receives.Enqueue(CloseNotSentPendingReceive);
            try
            {
                _receivesSem.Release();
            }
            catch
            { }
        }

        internal void ClientSend(List<JsonString> messages)
        {
            try
            {
                foreach (var message in messages)
                {
                    _receives.Enqueue(new PendingReceive(message));
                    _receivesSem.Release();
                }
            }
            catch // _receivesSem disposed
            { }
        }

        void CancelClientTimeout()
        {
            _clientTimeoutCts.Cancel();
            _clientTimeoutCts = null;
        }

        async void ScheduleClientTimeout()
        {
            var clientTimeoutCts = new CancellationTokenSource();
            _clientTimeoutCts = clientTimeoutCts;
            try
            {
                await Task.Delay(_options.DisconnectTimeout, clientTimeoutCts.Token);
            }
            catch
            { }
            if (clientTimeoutCts.IsCancellationRequested)
            {
                return;
            }
            if (_sessionManager.TryRemoveSession(this, clientTimeoutCts.Token))
            {
                HandleClientTimedOut();
            }
        }

        private async void HandleClientTimedOut()
        {
            await _sendEnqueueSem.WaitAsync();
            if (_sendState == SendOpen)
            {
                _sendState = SendClientTimeout;
            }
            _sendEnqueueSem.Release();

            await _sendDequeueSem.WaitAsync();
            PendingSend send;
            while (_sends.TryDequeue(out send))
            {
                send.CompleteClientTimeout();
            }
            _sendDequeueSem.Release();

            if (_closeMessage == null)
            {
                _receives.Enqueue(CloseNotSentPendingReceive);
            }
            else
            {
                _receives.Enqueue(CloseSentPendingReceive);
            }
            try
            {
                _receivesSem.Release();
            }
            catch
            { }
        }

        internal async Task<WebSocketReceiveResult> ReceiveAsync(ArraySegment<byte> buffer, CancellationToken cancellationToken)
        {
            int oldState = Interlocked.CompareExchange(ref _receiveState, ReceiveOne, ReceiveNone);
            if (oldState == ReceiveDisposed)
            {
                throw SessionWebSocket.NewDisposedException();
            }
            if (oldState == ReceiveCloseReceived)
            {
                throw new InvalidOperationException(ReceiverIsClosed);
            }
            if (oldState == ReceiveOne)
            {
                throw new InvalidOperationException(SimultaneousReceivesNotSupported);
            }

            try
            {
                await _receivesSem.WaitAsync(cancellationToken);
                PendingReceive receive = null;
                _receives.TryPeek(out receive);

                if (receive.Type == WebSocketMessageType.Text)
                {
                    try
                    {
                        int length = receive.TextMessage.Decode(buffer);

                        bool endOfMessage = receive.TextMessage.IsEmpty;
                        var result = new WebSocketReceiveResult(length, WebSocketMessageType.Text, endOfMessage);

                        if (endOfMessage)
                        {
                            _receives.TryDequeue(out receive);
                        }
                        else
                        {
                            // undo Wait
                            _receivesSem.Release();
                        }
                        return result;
                    }
                    catch // Decode exception
                    {
                        _receives.TryDequeue(out receive);
                        throw;
                    }

                }
                else // (receive.Type == WebSocketMessageType.Close)
                {
                    var result = new WebSocketReceiveResult(0, WebSocketMessageType.Close, true, receive.CloseStatus, receive.CloseStatusDescription);
                    _receiveState = ReceiveCloseReceived;
                    _receives.TryDequeue(out receive);
                    return result;
                }

            }
            finally
            {
                Interlocked.CompareExchange(ref _receiveState, ReceiveNone, ReceiveOne);
            }
        }

        public void ExitExclusiveLock()
        {
            _clientLock.ExitWriteLock();
        }

        public void EnterExclusiveLock()
        {
            _clientLock.EnterWriteLock();
        }

        public void ExitSharedLock()
        {
            _clientLock.ExitReadLock();
        }

        internal void EnterSharedLock()
        {
            _clientLock.EnterReadLock();
        }

        public Task SendCloseToClientAsync(WebSocketCloseStatus closeStatus, string statusDescription, CancellationToken cancellationToken)
        {
            return ServerSendMessageAsync(WebSocketMessageType.Close, new ArraySegment<byte>(Receiver.CloseBuffer(closeStatus, statusDescription)), cancellationToken);
        }

        private async Task ServerSendMessageAsync(WebSocketMessageType type, ArraySegment<byte> buffer, CancellationToken cancellationToken)
        {
            await _sendEnqueueSem.WaitAsync(cancellationToken);

            TaskCompletionSource<bool> tcs = new TaskCompletionSource<bool>();
            var send = new PendingSend(tcs, type, buffer, cancellationToken);

            try
            {
                if (_sendState == SendOpen)
                {
                    if (type == WebSocketMessageType.Close)
                    {
                        _sendState = SendCloseSent;
                    }
                    _sends.Enqueue(send);
                    _sendsSem.Release();
                }
                else
                {
                    if (_sendState == SendCloseSent)
                    {
                        send.CompleteCloseSent();
                    }
                    else if (_sendState == SendDisposed)
                    {
                        send.CompleteDisposed();
                    }
                    else if (_sendState == SendClientTimeout)
                    {
                        send.CompleteClientTimeout();
                    }
                }
            }
            finally
            {
                _sendEnqueueSem.Release();
            }

            await send.CompleteTask;
        }

        public Task ServerSendTextAsync(ArraySegment<byte> buffer, CancellationToken cancellationToken)
        {
            return ServerSendMessageAsync(WebSocketMessageType.Text, buffer, cancellationToken);
        }

        public async Task<WebSocket> AcceptWebSocket()
        {
            await _receiver.Open(true);

            _socket = new SessionWebSocket(this);
            return _socket;
        }
    }
}
