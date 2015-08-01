﻿// Copyright (C) 2015 Tom Deseyn
// Licensed under GNU LGPL, Version 2.1. See LICENSE in the project root for license information.

using System;

namespace Tmds.SockJS
{
    public class SockJSOptions
    {
        public SockJSOptions()
        {
            JSClientLibraryUrl = "http://cdn.jsdelivr.net/sockjs/0.3.4/sockjs.min.js";
            MaxResponseLength = 128 * 1024;
            UseWebSocket = true;
            SetJSessionIDCookie = false;
            HeartbeatInterval = TimeSpan.FromSeconds(25);
            DisconnectTimeout = TimeSpan.FromSeconds(50);
        }

        public string JSClientLibraryUrl { get; set; }

        public int MaxResponseLength{ get; set; }

        public bool UseWebSocket { get; set; }

        public bool SetJSessionIDCookie { get; set; }

        public TimeSpan HeartbeatInterval { get; set; }

        public TimeSpan DisconnectTimeout { get; set; }
    }
}
