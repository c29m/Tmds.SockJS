﻿// Copyright (C) 2015 Tom Deseyn. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using Microsoft.AspNet.Http.Features;
using System.Threading.Tasks;
using System.Net.WebSockets;

namespace Tmds.SockJS
{
    internal class SessionWebSocketFeature : IHttpWebSocketFeature
    {
        private Session _session;
        private TaskCompletionSource<bool> _acceptedTcs;
        public SessionWebSocketFeature(Session session)
        {
            _session = session;
            _acceptedTcs = new TaskCompletionSource<bool>();
        }

        public Task IsAcceptedPromise { get { return _acceptedTcs.Task; } }

        public bool IsWebSocketRequest
        {
            get
            {
                return true;
            }
        }

        public async Task<WebSocket> AcceptAsync(WebSocketAcceptContext context)
        {
            WebSocket ws = await _session.AcceptWebSocket();
            _acceptedTcs.SetResult(true);
            await Task.Yield(); // ensure _next returns
            return ws;
        }
    }
}
