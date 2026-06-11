// JS bridge between Magi.UnityTools.Net.WebSocketMagiTransport (WebGL
// variant) and the browser's native WebSocket API. System.Net.WebSockets
// is a no-op under Unity's WebGL runtime; these functions give the C#
// side a handle-based queue it can poll every frame instead.
//
// Contract expectations:
// - Handles are positive ints; 0 means "open failed" (caller must not
//   treat 0 as a live handle).
// - The receive queue stores both data frames and the close sentinel in
//   order — the C# side drains them FIFO via MagiWs_HasFrame / ReadFrame
//   / ConsumeClose so the close event doesn't race ahead of pending data.
// - Text messages are UTF-8 encoded and delivered as binary to the C#
//   side; the codec on both platforms speaks bytes.

var MagiWsLib = {
  $MagiWsState: {
    sockets: {},
    nextHandle: 1
  },

  MagiWs_Open__deps: ['$MagiWsState'],
  MagiWs_Open: function(urlPtr) {
    var url = UTF8ToString(urlPtr);
    try {
      var ws = new WebSocket(url);
      ws.binaryType = 'arraybuffer';
      var h = MagiWsState.nextHandle++;
      var s = {
        ws: ws,
        queue: [],
        closeCode: 0,
        closeReason: '',
        errored: false
      };
      ws.onmessage = function(ev) {
        if (typeof ev.data === 'string') {
          var enc = new TextEncoder().encode(ev.data);
          s.queue.push({ type: 1, data: enc });
        } else {
          s.queue.push({ type: 1, data: new Uint8Array(ev.data) });
        }
      };
      ws.onclose = function(ev) {
        s.closeCode = ev.code | 0;
        s.closeReason = ev.reason || '';
        s.queue.push({ type: 2, data: null });
      };
      ws.onerror = function() {
        s.errored = true;
      };
      MagiWsState.sockets[h] = s;
      return h;
    } catch (e) {
      return 0;
    }
  },

  MagiWs_State__deps: ['$MagiWsState'],
  MagiWs_State: function(h) {
    var s = MagiWsState.sockets[h];
    if (!s) return 4;
    return s.ws.readyState;
  },

  MagiWs_HasError__deps: ['$MagiWsState'],
  MagiWs_HasError: function(h) {
    var s = MagiWsState.sockets[h];
    if (!s) return 1;
    return s.errored ? 1 : 0;
  },

  MagiWs_HasFrame__deps: ['$MagiWsState'],
  MagiWs_HasFrame: function(h) {
    var s = MagiWsState.sockets[h];
    if (!s) return 3;
    if (s.queue.length === 0) return 0;
    return s.queue[0].type;
  },

  MagiWs_FrameLen__deps: ['$MagiWsState'],
  MagiWs_FrameLen: function(h) {
    var s = MagiWsState.sockets[h];
    if (!s || s.queue.length === 0) return 0;
    var f = s.queue[0];
    if (f.type !== 1) return 0;
    return f.data.length;
  },

  MagiWs_ReadFrame__deps: ['$MagiWsState'],
  MagiWs_ReadFrame: function(h, outPtr, maxLen) {
    var s = MagiWsState.sockets[h];
    if (!s || s.queue.length === 0) return 0;
    var f = s.queue[0];
    if (f.type !== 1) return 0;
    if (f.data.length > maxLen) return -1;
    HEAPU8.set(f.data, outPtr);
    s.queue.shift();
    return f.data.length;
  },

  MagiWs_ConsumeClose__deps: ['$MagiWsState'],
  MagiWs_ConsumeClose: function(h) {
    var s = MagiWsState.sockets[h];
    if (!s || s.queue.length === 0) return;
    if (s.queue[0].type === 2) s.queue.shift();
  },

  MagiWs_CloseCode__deps: ['$MagiWsState'],
  MagiWs_CloseCode: function(h) {
    var s = MagiWsState.sockets[h];
    if (!s) return 0;
    return s.closeCode | 0;
  },

  MagiWs_CopyCloseReason__deps: ['$MagiWsState'],
  MagiWs_CopyCloseReason: function(h, outPtr, maxLen) {
    var s = MagiWsState.sockets[h];
    if (!s || !s.closeReason) return 0;
    var len = lengthBytesUTF8(s.closeReason);
    if (len + 1 > maxLen) return -1;
    stringToUTF8(s.closeReason, outPtr, maxLen);
    return len;
  },

  MagiWs_Send__deps: ['$MagiWsState'],
  MagiWs_Send: function(h, dataPtr, len) {
    var s = MagiWsState.sockets[h];
    if (!s || s.ws.readyState !== 1) return 0;
    try {
      // Copy out of HEAPU8 before handing to ws.send — the browser may
      // retain the buffer and HEAPU8 can be invalidated by a later
      // allocation.
      var copy = new Uint8Array(HEAPU8.subarray(dataPtr, dataPtr + len));
      s.ws.send(copy.buffer);
      return 1;
    } catch (e) {
      return 0;
    }
  },

  MagiWs_Close__deps: ['$MagiWsState'],
  MagiWs_Close: function(h, code, reasonPtr) {
    var s = MagiWsState.sockets[h];
    if (!s) return;
    try {
      var reason = reasonPtr ? UTF8ToString(reasonPtr) : '';
      if (s.ws.readyState === 0 || s.ws.readyState === 1) {
        s.ws.close(code || 1000, reason);
      }
    } catch (e) {}
  },

  MagiWs_Dispose__deps: ['$MagiWsState'],
  MagiWs_Dispose: function(h) {
    var s = MagiWsState.sockets[h];
    if (!s) return;
    try {
      if (s.ws.readyState === 0 || s.ws.readyState === 1) s.ws.close(1000, '');
    } catch (e) {}
    delete MagiWsState.sockets[h];
  },

  // Fetch bridge. HttpClient+UnityTLS are non-functional on Unity
  // WebGL, so the C# transport routes its one REST call through this
  // handle-based fetch wrapper. State: 0=init, 1=pending, 2=done,
  // 3=error. The C# side polls MagiHttp_State every frame.
  $MagiHttpState: {
    requests: {},
    nextHandle: 1
  },

  MagiHttp_Post__deps: ['$MagiHttpState'],
  MagiHttp_Post: function(urlPtr, contentTypePtr, bodyPtr, bodyLen) {
    var url = UTF8ToString(urlPtr);
    var contentType = UTF8ToString(contentTypePtr);
    // Copy the body out of HEAPU8 before dispatching — HEAPU8 can be
    // invalidated by later allocations while fetch is in flight.
    var body = new Uint8Array(HEAPU8.subarray(bodyPtr, bodyPtr + bodyLen));
    var h = MagiHttpState.nextHandle++;
    var r = { state: 1, status: 0, body: null, error: '' };
    MagiHttpState.requests[h] = r;
    try {
      fetch(url, {
        method: 'POST',
        headers: { 'Content-Type': contentType },
        body: body.buffer
      }).then(function(resp) {
        r.status = resp.status | 0;
        return resp.arrayBuffer();
      }).then(function(buf) {
        r.body = new Uint8Array(buf);
        r.state = 2;
      }).catch(function(err) {
        r.error = String(err && err.message || err);
        r.state = 3;
      });
    } catch (e) {
      r.error = String(e && e.message || e);
      r.state = 3;
    }
    return h;
  },

  MagiHttp_State__deps: ['$MagiHttpState'],
  MagiHttp_State: function(h) {
    var r = MagiHttpState.requests[h];
    if (!r) return 3;
    return r.state;
  },

  MagiHttp_Status__deps: ['$MagiHttpState'],
  MagiHttp_Status: function(h) {
    var r = MagiHttpState.requests[h];
    if (!r) return 0;
    return r.status | 0;
  },

  MagiHttp_BodyLen__deps: ['$MagiHttpState'],
  MagiHttp_BodyLen: function(h) {
    var r = MagiHttpState.requests[h];
    if (!r || !r.body) return 0;
    return r.body.length;
  },

  MagiHttp_ReadBody__deps: ['$MagiHttpState'],
  MagiHttp_ReadBody: function(h, outPtr, maxLen) {
    var r = MagiHttpState.requests[h];
    if (!r || !r.body) return 0;
    if (r.body.length > maxLen) return -1;
    HEAPU8.set(r.body, outPtr);
    return r.body.length;
  },

  MagiHttp_CopyError__deps: ['$MagiHttpState'],
  MagiHttp_CopyError: function(h, outPtr, maxLen) {
    var r = MagiHttpState.requests[h];
    if (!r || !r.error) return 0;
    var len = lengthBytesUTF8(r.error);
    if (len + 1 > maxLen) return -1;
    stringToUTF8(r.error, outPtr, maxLen);
    return len;
  },

  MagiHttp_Dispose__deps: ['$MagiHttpState'],
  MagiHttp_Dispose: function(h) {
    delete MagiHttpState.requests[h];
  },

  // Direct console.log bridge for the noEngineReferences asmdef — routes
  // through JS so diagnostic output doesn't depend on Unity's managed
  // Console.Out redirection (which silently drops on some Firefox builds).
  MagiLog: function(msgPtr) {
    try { console.log(UTF8ToString(msgPtr)); } catch (e) { }
  }
};

autoAddDeps(MagiWsLib, '$MagiWsState');
autoAddDeps(MagiWsLib, '$MagiHttpState');
mergeInto(LibraryManager.library, MagiWsLib);
