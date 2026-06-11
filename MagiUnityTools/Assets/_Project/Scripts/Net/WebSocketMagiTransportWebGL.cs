#if UNITY_WEBGL && !UNITY_EDITOR
using System;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using MagiGameServer.Codec;
using MagiGameServer.Contracts.Core;
using MagiGameServer.Contracts.Protocol;
using static Magi.UnityTools.Net.MagiWsInterop;

namespace Magi.UnityTools.Net
{
    /// WebGL replacement for the ClientWebSocket-based transport.
    /// System.Net.WebSockets.ClientWebSocket is not functional under
    /// Unity's WebGL runtime — it relies on threads and raw sockets the
    /// browser sandbox doesn't expose. This implementation routes through
    /// the small JSLIB bridge at Plugins/WebGL/MagiWs.jslib that wraps
    /// the browser's native WebSocket API. The C# layer polls the
    /// JS-side queue on a Task loop; in WebGL that loop runs cooperatively
    /// on the main thread, so no cross-thread dispatch is needed.
    ///
    /// Public API (OpenSession / Attach / Reattach / ClaimAndAttach /
    /// SendAsync / DisposeAsync / OnFrame / OnTransportError) matches the
    /// native-platform WebSocketMagiTransport byte-for-byte, so callers
    /// (LedgeBoardSessionDriver, MagiSession) don't branch — Unity's
    /// platform #if excludes the sibling file on WebGL.
    // DllImport cannot live on a generic (or generic-contained) method —
    // CS7042. Park the JSLIB bindings on a non-generic static helper and
    // call through it from the generic transport.
    internal static class MagiWsInterop
    {
        [DllImport("__Internal")] internal static extern int MagiWs_Open(string url);
        [DllImport("__Internal")] internal static extern int MagiWs_State(int handle);
        [DllImport("__Internal")] internal static extern int MagiWs_HasError(int handle);
        [DllImport("__Internal")] internal static extern int MagiWs_HasFrame(int handle);
        [DllImport("__Internal")] internal static extern int MagiWs_FrameLen(int handle);
        [DllImport("__Internal")] internal static extern int MagiWs_ReadFrame(int handle, byte[] buf, int maxLen);
        [DllImport("__Internal")] internal static extern void MagiWs_ConsumeClose(int handle);
        [DllImport("__Internal")] internal static extern int MagiWs_CloseCode(int handle);
        [DllImport("__Internal")] internal static extern int MagiWs_CopyCloseReason(int handle, byte[] buf, int maxLen);
        [DllImport("__Internal")] internal static extern int MagiWs_Send(int handle, byte[] data, int len);
        [DllImport("__Internal")] internal static extern void MagiWs_Close(int handle, int code, string reason);
        [DllImport("__Internal")] internal static extern void MagiWs_Dispose(int handle);

        // Fetch bridge. State codes: 0=init, 1=pending, 2=done, 3=error.
        [DllImport("__Internal")] internal static extern int MagiHttp_Post(string url, string contentType, byte[] body, int bodyLen);
        [DllImport("__Internal")] internal static extern int MagiHttp_State(int handle);
        [DllImport("__Internal")] internal static extern int MagiHttp_Status(int handle);
        [DllImport("__Internal")] internal static extern int MagiHttp_BodyLen(int handle);
        [DllImport("__Internal")] internal static extern int MagiHttp_ReadBody(int handle, byte[] buf, int maxLen);
        [DllImport("__Internal")] internal static extern int MagiHttp_CopyError(int handle, byte[] buf, int maxLen);
        [DllImport("__Internal")] internal static extern void MagiHttp_Dispose(int handle);

        [DllImport("__Internal")] internal static extern void MagiLog(string msg);
    }

    public sealed class WebSocketMagiTransport<TState, TAction> : IMagiTransport<TState, TAction>
    {

        // WebSocket.readyState values: 0=Connecting, 1=Open, 2=Closing,
        // 3=Closed. JSLIB returns 4 when the handle doesn't resolve.
        private const int StateConnecting = 0;
        private const int StateOpen = 1;
        private const int StateClosing = 2;
        private const int StateClosed = 3;
        private const int StateInvalid = 4;

        // ~one frame at 60Hz. The browser WebSocket callbacks run on the
        // main JS thread between C# yields, so a short delay lets them
        // enqueue frames without turning the loop into a spin wait.
        private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(16);

        // Matches the native transport's close-handshake cap so shutdown
        // timing looks the same across platforms.
        private static readonly TimeSpan CloseHandshakeTimeout = TimeSpan.FromSeconds(3);

        // HttpClient is non-functional on Unity WebGL — it pulls in
        // UnityTLS which tries to Marshal.PtrToStructure through an
        // icall signature the wasm module rejects at runtime ("indirect
        // call signature mismatch"). The WebGL transport routes REST
        // through a small fetch bridge in MagiWs.jslib (MagiHttp_*).
        // The HttpClient parameter on the ctor stays only for API
        // parity with the native transport — it's ignored.
        private readonly SessionId? _presetSession;
        private int _handle;
        private Task _receiveTask;
        private CancellationTokenSource _cts;
        private string _baseUri;
        private int _disposed;

        public event Action<ServerFrame<TState>> OnFrame;
        public event Action<Exception> OnTransportError;

        public WebSocketMagiTransport() { }

        public WebSocketMagiTransport(HttpClient http) { }

        public WebSocketMagiTransport(HttpClient http, string baseUri, SessionId session)
        {
            if (string.IsNullOrEmpty(baseUri)) throw new ArgumentException("baseUri required", nameof(baseUri));
            _baseUri = baseUri.TrimEnd('/');
            _presetSession = session;
        }

        public async Task<SessionId> OpenSessionAsync(MagiSessionConfig config, CancellationToken ct)
        {
            if (_presetSession.HasValue)
            {
                MagiLog("[tx-diag] OpenSessionAsync: preset session path, returning " + _presetSession.Value.Value);
                return _presetSession.Value;
            }
            if (config == null) throw new ArgumentNullException(nameof(config));
            if (string.IsNullOrEmpty(config.BaseUri)) throw new ArgumentException("BaseUri required", nameof(config));
            _baseUri = config.BaseUri.TrimEnd('/');

            var req = new OpenSessionRequest
            {
                GameId = config.GameId,
                SeatCount = config.SeatCount,
                Seed = config.Seed,
                Options = config.Options,
            };
            var body = EnvelopeCodec.Serialize(req);
            MagiLog("[tx-diag] OpenSessionAsync: posting /session/open bodyLen=" + body.Length);
            byte[] payload = await PostJsonAsync(_baseUri + "/session/open", body, ct);
            MagiLog("[tx-diag] OpenSessionAsync: POST returned payloadLen=" + payload.Length);
            var parsed = EnvelopeCodec.Deserialize<OpenSessionResponse>(payload);
            if (parsed == null) throw new InvalidOperationException("Server returned empty OpenSessionResponse");
            MagiLog("[tx-diag] OpenSessionAsync: parsed session=" + (parsed.Session.Value ?? "<null>") + " seats=" + parsed.SeatCount);
            return parsed.Session;
        }

        private static async Task<byte[]> PostJsonAsync(string url, byte[] body, CancellationToken ct)
        {
            int h = MagiHttp_Post(url, "application/json", body, body.Length);
            MagiLog("[tx-diag] PostJsonAsync: dispatched fetch handle=" + h + " url=" + url);
            if (h == 0) throw new InvalidOperationException($"POST {url}: failed to dispatch fetch");
            try
            {
                int polls = 0;
                while (true)
                {
                    ct.ThrowIfCancellationRequested();
                    int state = MagiHttp_State(h);
                    if (state == 2) break;               // done
                    if (state == 3) throw new InvalidOperationException($"POST {url}: fetch errored: {ReadHttpError(h)}");
                    if (polls < 5 || polls % 200 == 199) MagiLog("[tx-diag] PostJsonAsync: polls=" + (polls + 1) + " state=" + state);
                    polls++;
                    await Task.Yield();
                }
                MagiLog("[tx-diag] PostJsonAsync: fetch resolved after polls=" + polls);
                int status = MagiHttp_Status(h);
                if (status < 200 || status >= 300) throw new InvalidOperationException($"POST {url}: http {status}");
                int len = MagiHttp_BodyLen(h);
                if (len <= 0) return Array.Empty<byte>();
                var buf = new byte[len];
                int copied = MagiHttp_ReadBody(h, buf, len);
                if (copied != len) throw new InvalidOperationException($"POST {url}: body copy short ({copied}/{len})");
                return buf;
            }
            finally
            {
                MagiHttp_Dispose(h);
            }
        }

        private static string ReadHttpError(int h)
        {
            var buf = new byte[512];
            int n = MagiHttp_CopyError(h, buf, buf.Length);
            return n > 0 ? Encoding.UTF8.GetString(buf, 0, n) : "unknown";
        }

        public async Task AttachAsync(SessionId session, SeatId seat, CancellationToken ct)
        {
            MagiLog("[tx-diag] AttachAsync enter session=" + (session.Value ?? "<null>") + " seat=" + seat.Value);
            EnsureNotAttached();
            var uri = BuildWebSocketUri(session, seat);
            MagiLog("[tx-diag] AttachAsync built uri=" + uri);
            await ConnectAsync(uri, ct);
            MagiLog("[tx-diag] AttachAsync ConnectAsync returned, starting receive loop");
            _receiveTask = RunReceiveLoop();
        }

        public async Task ReattachAsync(SessionId session, SeatId seat, string reconnectToken, CancellationToken ct)
        {
            EnsureNotAttached();
            if (string.IsNullOrEmpty(reconnectToken)) throw new ArgumentException("reconnectToken required", nameof(reconnectToken));
            await ConnectAsync(BuildWebSocketReattachUri(session, seat, reconnectToken), ct);
            _receiveTask = RunReceiveLoop();
        }

        public async Task<SeatId> ClaimAndAttachAsync(SessionId session, CancellationToken ct)
        {
            EnsureNotAttached();
            await ConnectAsync(BuildWebSocketUriNoSeat(session), ct);

            // Peel first frame (JoinSnapshot) before starting the poll
            // loop so the caller learns which seat the server assigned.
            // Matches the native transport's sync-first-frame shape.
            ServerFrame<TState> first = await PeelFirstFrameAsync(ct);
            if (first == null || first.Kind != ServerFrameKind.JoinSnapshot || first.JoinSnapshot == null)
                throw new InvalidOperationException("Expected JoinSnapshot as first frame on claim attach");
            OnFrame?.Invoke(first);
            _receiveTask = RunReceiveLoop();
            return first.JoinSnapshot.ForSeat;
        }

        public Task SendAsync(ActionEnvelope<TAction> envelope, CancellationToken ct)
        {
            if (envelope == null) throw new ArgumentNullException(nameof(envelope));
            var frame = new ClientFrame<TAction> { Kind = ClientFrameKind.Action, Action = envelope };
            return SendFrameAsync(frame, ct);
        }

        public Task SendAsync(TakebackRequest request, CancellationToken ct)
        {
            if (request == null) throw new ArgumentNullException(nameof(request));
            var frame = new ClientFrame<TAction> { Kind = ClientFrameKind.Takeback, Takeback = request };
            return SendFrameAsync(frame, ct);
        }

        private Task SendFrameAsync(ClientFrame<TAction> frame, CancellationToken ct)
        {
            if (_handle == 0 || MagiWs_State(_handle) != StateOpen)
                throw new InvalidOperationException("WebSocket not open; AttachAsync must complete first");

            var bytes = EnvelopeCodec.Serialize(frame);
            // Browser WebSocket.send is synchronous from the caller's
            // POV — it queues the payload. A 0 return means the socket
            // transitioned out of Open between the state check above and
            // the call; surface it so the caller can react.
            int ok = MagiWs_Send(_handle, bytes, bytes.Length);
            if (ok == 0) throw new InvalidOperationException("WebSocket send failed (socket closed)");
            return Task.CompletedTask;
        }

        private void EnsureNotAttached()
        {
            if (_handle != 0) throw new InvalidOperationException("AttachAsync already called");
            if (string.IsNullOrEmpty(_baseUri)) throw new InvalidOperationException("OpenSessionAsync must run before AttachAsync");
        }

        private async Task ConnectAsync(Uri uri, CancellationToken ct)
        {
            _cts = new CancellationTokenSource();
            MagiLog("[tx-diag] ConnectAsync calling MagiWs_Open(" + uri + ")");
            _handle = MagiWs_Open(uri.ToString());
            MagiLog("[tx-diag] MagiWs_Open returned handle=" + _handle);
            if (_handle == 0) throw new InvalidOperationException("Failed to open WebSocket");

            int polls = 0;
            while (true)
            {
                ct.ThrowIfCancellationRequested();
                int state = MagiWs_State(_handle);
                if (state == StateOpen)
                {
                    MagiLog("[tx-diag] ConnectAsync socket open after " + polls + " polls");
                    return;
                }
                if (state == StateClosing || state == StateClosed || state == StateInvalid)
                    throw new InvalidOperationException("WebSocket closed during connect (" + DescribeClose() + ")");
                if (MagiWs_HasError(_handle) != 0)
                    throw new InvalidOperationException("WebSocket errored during connect");
                if (polls == 0 || polls == 30 || polls == 120)
                    MagiLog("[tx-diag] ConnectAsync waiting polls=" + polls + " state=" + state);
                polls++;
                await Task.Yield();
            }
        }

        private async Task<ServerFrame<TState>> PeelFirstFrameAsync(CancellationToken ct)
        {
            while (true)
            {
                ct.ThrowIfCancellationRequested();
                _cts.Token.ThrowIfCancellationRequested();
                int head = MagiWs_HasFrame(_handle);
                if (head == 1)
                {
                    byte[] payload = ReadFrameBytes();
                    if (payload == null) throw new InvalidOperationException("Failed to drain first frame");
                    try
                    {
                        return EnvelopeCodec.Deserialize<ServerFrame<TState>>(payload);
                    }
                    catch (Exception ex)
                    {
                        throw new InvalidOperationException("First frame on claim attach failed to decode", ex);
                    }
                }
                if (head == 2)
                {
                    MagiWs_ConsumeClose(_handle);
                    throw new InvalidOperationException("Server rejected attach: " + DescribeClose());
                }
                if (head == 3) throw new InvalidOperationException("WebSocket handle invalidated during attach");
                await Task.Yield();
            }
        }

        private async Task RunReceiveLoop()
        {
            // In WebGL Unity this runs on the single main thread through
            // the cooperative scheduler. Task.Delay yields back to the JS
            // event loop so onmessage / onclose handlers can enqueue
            // frames between iterations.
            try
            {
                while (!_cts.IsCancellationRequested)
                {
                    int head = MagiWs_HasFrame(_handle);
                    if (head == 0)
                    {
                        await Task.Yield();
                        continue;
                    }
                    if (head == 2)
                    {
                        MagiWs_ConsumeClose(_handle);
                        return;
                    }
                    if (head == 3) return;

                    byte[] payload = ReadFrameBytes();
                    if (payload == null) continue;
                    try
                    {
                        var frame = EnvelopeCodec.Deserialize<ServerFrame<TState>>(payload);
                        if (frame != null) OnFrame?.Invoke(frame);
                    }
                    catch (Exception ex)
                    {
                        OnTransportError?.Invoke(ex);
                    }
                }
            }
            catch (OperationCanceledException) { }
            catch (Exception ex) { OnTransportError?.Invoke(ex); }
        }

        private byte[] ReadFrameBytes()
        {
            int len = MagiWs_FrameLen(_handle);
            if (len <= 0) return null;
            var buf = new byte[len];
            int copied = MagiWs_ReadFrame(_handle, buf, len);
            if (copied <= 0) return null;
            if (copied == len) return buf;
            var exact = new byte[copied];
            Buffer.BlockCopy(buf, 0, exact, 0, copied);
            return exact;
        }

        private string DescribeClose()
        {
            if (_handle == 0) return "no handle";
            int code = MagiWs_CloseCode(_handle);
            var reasonBuf = new byte[256];
            int copied = MagiWs_CopyCloseReason(_handle, reasonBuf, reasonBuf.Length);
            string reason = copied > 0 ? Encoding.UTF8.GetString(reasonBuf, 0, copied) : "";
            return string.IsNullOrEmpty(reason) ? ("code=" + code) : (reason + " (code=" + code + ")");
        }

        private Uri BuildWebSocketUri(SessionId session, SeatId seat)
            => new Uri(BuildBaseWebSocketUri(session) + "?seat=" + seat.Value);

        private Uri BuildWebSocketUriNoSeat(SessionId session)
            => new Uri(BuildBaseWebSocketUri(session));

        private Uri BuildWebSocketReattachUri(SessionId session, SeatId seat, string token)
            => new Uri(BuildBaseWebSocketUri(session) + "?seat=" + seat.Value + "&reattach=" + Uri.EscapeDataString(token));

        private string BuildBaseWebSocketUri(SessionId session)
        {
            string scheme = "ws";
            string rest = _baseUri;
            int schemeEnd = _baseUri.IndexOf("://", StringComparison.Ordinal);
            if (schemeEnd >= 0)
            {
                var proto = _baseUri.Substring(0, schemeEnd);
                scheme = string.Equals(proto, "https", StringComparison.OrdinalIgnoreCase) ? "wss" : "ws";
                rest = _baseUri.Substring(schemeEnd + 3);
            }
            return $"{scheme}://{rest}/session/{session.Value}";
        }

        public async ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
            try { _cts?.Cancel(); } catch { }

            if (_handle != 0)
            {
                int state = MagiWs_State(_handle);
                if (state == StateOpen || state == StateConnecting)
                {
                    try { MagiWs_Close(_handle, 1000, "bye"); } catch { }
                }
            }

            if (_receiveTask != null)
            {
                // Bound receive-task wait the same way the native transport
                // does — a hung onclose shouldn't pin shutdown.
                var completed = await Task.WhenAny(_receiveTask, Task.Delay(CloseHandshakeTimeout));
                if (completed == _receiveTask)
                {
                    try { await _receiveTask; } catch { }
                }
            }

            if (_handle != 0)
            {
                try { MagiWs_Dispose(_handle); } catch { }
                _handle = 0;
            }
            _cts?.Dispose();
        }
    }
}
#endif
