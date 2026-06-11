#if !UNITY_WEBGL || UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.WebSockets;
using System.Threading;
using System.Threading.Tasks;
using MagiGameServer.Codec;
using MagiGameServer.Contracts.Core;
using MagiGameServer.Contracts.Protocol;

namespace Magi.UnityTools.Net
{
    /// Real-socket implementation of IMagiTransport. All wire-shape work
    /// (JSON bytes ↔ ClientFrame / ServerFrame) is confined to this class
    /// so MagiSession sees only typed frames — the one place the codec
    /// is loaded.
    ///
    /// Sends are serialized with a SemaphoreSlim because ClientWebSocket
    /// forbids concurrent SendAsync calls and the dispatcher can raise
    /// OutgoingAction / OutgoingTakeback back-to-back from the main
    /// thread. The receive loop runs as a single background Task so
    /// ReceiveAsync is likewise never called concurrently.
    public sealed class WebSocketMagiTransport<TState, TAction> : IMagiTransport<TState, TAction>
    {
        // Shutdown cap: if the server doesn't acknowledge the close frame
        // or the background receive task doesn't unwind within this
        // window, DisposeAsync falls through to Abort() + fire-and-forget
        // on the receive task so the caller's shutdown path isn't pinned
        // by an unresponsive peer. Three seconds matches the server-side
        // SessionShutdownHostedService drain budget on the host so both
        // ends give up at the same grain.
        private static readonly TimeSpan CloseHandshakeTimeout = TimeSpan.FromSeconds(3);

        private readonly HttpClient _http;
        private readonly bool _ownsHttp;
        private readonly SemaphoreSlim _sendLock = new SemaphoreSlim(1, 1);
        private readonly SessionId? _presetSession;
        private ClientWebSocket _ws;
        private Task _receiveTask;
        private CancellationTokenSource _cts;
        private string _baseUri;
        private int _disposed;

        public event Action<ServerFrame<TState>> OnFrame;
        public event Action<Exception> OnTransportError;

        public WebSocketMagiTransport() : this(null) { }

        /// Accepts an externally-owned HttpClient so callers can control
        /// pooling, base handlers, timeouts, auth headers. Passing null
        /// creates (and disposes) a private client.
        public WebSocketMagiTransport(HttpClient http)
        {
            _http = http ?? new HttpClient();
            _ownsHttp = http == null;
        }

        /// Secondary-seat ctor for multi-seat drivers that open one server
        /// session and attach N sockets to it. Pre-seeds the base URI and
        /// the SessionId so OpenSessionAsync can skip the POST — only the
        /// primary seat's transport should POST /session/open, the rest
        /// reuse the result. Callers still invoke MagiSession.ConnectAsync
        /// the same way; the transport just returns the known SessionId
        /// without hitting the wire.
        public WebSocketMagiTransport(HttpClient http, string baseUri, SessionId session)
            : this(http)
        {
            if (string.IsNullOrEmpty(baseUri)) throw new ArgumentException("baseUri required", nameof(baseUri));
            _baseUri = baseUri.TrimEnd('/');
            _presetSession = session;
        }

        public async Task<SessionId> OpenSessionAsync(MagiSessionConfig config, CancellationToken ct)
        {
            if (_presetSession.HasValue)
            {
                // Secondary path: the driver already POSTed and handed us
                // the SessionId. Config.BaseUri may be null on this path;
                // ctor pre-seeded _baseUri for AttachAsync.
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
            using var content = new ByteArrayContent(body);
            content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
            using var resp = await _http.PostAsync(_baseUri + "/session/open", content, ct).ConfigureAwait(false);
            resp.EnsureSuccessStatusCode();
            var payload = await resp.Content.ReadAsByteArrayAsync().ConfigureAwait(false);
            var parsed = EnvelopeCodec.Deserialize<OpenSessionResponse>(payload);
            if (parsed == null) throw new InvalidOperationException("Server returned empty OpenSessionResponse");
            return parsed.Session;
        }

        public async Task AttachAsync(SessionId session, SeatId seat, CancellationToken ct)
        {
            if (_ws != null) throw new InvalidOperationException("AttachAsync already called");
            if (string.IsNullOrEmpty(_baseUri)) throw new InvalidOperationException("OpenSessionAsync must run before AttachAsync");

            _ws = new ClientWebSocket();
            _cts = new CancellationTokenSource();
            await _ws.ConnectAsync(BuildWebSocketUri(session, seat), ct).ConfigureAwait(false);
            _receiveTask = Task.Run(ReceiveLoopAsync);
        }

        public async Task ReattachAsync(SessionId session, SeatId seat, string reconnectToken, CancellationToken ct)
        {
            if (_ws != null) throw new InvalidOperationException("AttachAsync already called");
            if (string.IsNullOrEmpty(_baseUri)) throw new InvalidOperationException("OpenSessionAsync must run before AttachAsync");
            if (string.IsNullOrEmpty(reconnectToken)) throw new ArgumentException("reconnectToken required", nameof(reconnectToken));

            _ws = new ClientWebSocket();
            _cts = new CancellationTokenSource();
            // Server rejection (token_mismatch, seat_already_attached)
            // surfaces here as a WebSocketException because ConnectAsync
                // sees the handshake completing but the subsequent close
            // frame propagates up. Callers catch and fall through to
            // ClaimAndAttachAsync.
            await _ws.ConnectAsync(BuildWebSocketReattachUri(session, seat, reconnectToken), ct).ConfigureAwait(false);
            _receiveTask = Task.Run(ReceiveLoopAsync);
        }

        public async Task<SeatId> ClaimAndAttachAsync(SessionId session, CancellationToken ct)
        {
            if (_ws != null) throw new InvalidOperationException("AttachAsync already called");
            if (string.IsNullOrEmpty(_baseUri)) throw new InvalidOperationException("OpenSessionAsync must run before AttachAsync");

            _ws = new ClientWebSocket();
            _cts = new CancellationTokenSource();
            await _ws.ConnectAsync(BuildWebSocketUriNoSeat(session), ct).ConfigureAwait(false);

            // Peel the first frame off synchronously so the caller knows
            // which seat the server assigned. The receive loop is started
            // only after this frame, so ReceiveAsync isn't called
            // concurrently (WebSocket forbids that). A Close frame here
            // means the server rejected the claim (session_full) — we
            // throw with the close reason so the caller can report it.
            var buffer = new byte[8192];
            var accumulator = new List<byte>();
            WebSocketReceiveResult result;
            do
            {
                result = await _ws.ReceiveAsync(new ArraySegment<byte>(buffer), ct).ConfigureAwait(false);
                if (result.MessageType == WebSocketMessageType.Close)
                {
                    var reason = _ws.CloseStatusDescription ?? _ws.CloseStatus?.ToString() ?? "closed";
                    throw new InvalidOperationException("Server rejected attach: " + reason);
                }
                for (int i = 0; i < result.Count; i++) accumulator.Add(buffer[i]);
            } while (!result.EndOfMessage);

            ServerFrame<TState> frame;
            try
            {
                frame = EnvelopeCodec.Deserialize<ServerFrame<TState>>(accumulator.ToArray());
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException("First frame on claim attach failed to decode", ex);
            }
            if (frame == null || frame.Kind != ServerFrameKind.JoinSnapshot || frame.JoinSnapshot == null)
                throw new InvalidOperationException("Expected JoinSnapshot as first frame on claim attach");

            // Deliver the JoinSnapshot through the normal OnFrame path so
            // MagiSession enqueues it. Dispatcher isn't set yet — MagiSession
            // just queues in _inbound and drains on the next Tick(), same
            // shape as explicit-seat attach.
            OnFrame?.Invoke(frame);
            _receiveTask = Task.Run(ReceiveLoopAsync);
            return frame.JoinSnapshot.ForSeat;
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

        private async Task SendFrameAsync(ClientFrame<TAction> frame, CancellationToken ct)
        {
            if (_ws == null || _ws.State != WebSocketState.Open)
                throw new InvalidOperationException("WebSocket not open; AttachAsync must complete first");

            var bytes = EnvelopeCodec.Serialize(frame);
            await _sendLock.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                await _ws.SendAsync(new ArraySegment<byte>(bytes), WebSocketMessageType.Text, endOfMessage: true, ct).ConfigureAwait(false);
            }
            finally
            {
                _sendLock.Release();
            }
        }

        private async Task ReceiveLoopAsync()
        {
            var buffer = new byte[8192];
            var accumulator = new List<byte>();
            try
            {
                while (_ws.State == WebSocketState.Open && !_cts.IsCancellationRequested)
                {
                    accumulator.Clear();
                    WebSocketReceiveResult result;
                    do
                    {
                        result = await _ws.ReceiveAsync(new ArraySegment<byte>(buffer), _cts.Token).ConfigureAwait(false);
                        if (result.MessageType == WebSocketMessageType.Close) return;
                        if (result.Count > 0)
                        {
                            for (int i = 0; i < result.Count; i++) accumulator.Add(buffer[i]);
                        }
                    } while (!result.EndOfMessage);

                    if (accumulator.Count == 0) continue;
                    try
                    {
                        var frame = EnvelopeCodec.Deserialize<ServerFrame<TState>>(accumulator.ToArray());
                        if (frame != null) OnFrame?.Invoke(frame);
                    }
                    catch (Exception ex)
                    {
                        OnTransportError?.Invoke(ex);
                    }
                }
            }
            catch (OperationCanceledException) { }
            catch (WebSocketException ex) { OnTransportError?.Invoke(ex); }
            catch (Exception ex) { OnTransportError?.Invoke(ex); }
        }

        private Uri BuildWebSocketUri(SessionId session, SeatId seat)
            => new Uri(BuildBaseWebSocketUri(session) + "?seat=" + seat.Value);

        private Uri BuildWebSocketUriNoSeat(SessionId session)
            => new Uri(BuildBaseWebSocketUri(session));

        private Uri BuildWebSocketReattachUri(SessionId session, SeatId seat, string token)
            => new Uri(BuildBaseWebSocketUri(session) + "?seat=" + seat.Value + "&reattach=" + Uri.EscapeDataString(token));

        private string BuildBaseWebSocketUri(SessionId session)
        {
            // Derive ws:// from http:// (wss:// from https://). Falls back to
            // inserting ws:// if BaseUri didn't include a scheme — but
            // OpenSessionAsync already threw on empty, so this is defensive.
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

            if (_ws != null && (_ws.State == WebSocketState.Open || _ws.State == WebSocketState.CloseReceived))
            {
                // Bound the close handshake — a server that has stopped
                // acking frames (hung process, dropped route) would
                // otherwise pin this await indefinitely and the Unity
                // player's shutdown path would wedge on it.
                using var closeCts = new CancellationTokenSource(CloseHandshakeTimeout);
                try
                {
                    await _ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "bye", closeCts.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    try { _ws.Abort(); } catch { }
                }
                catch
                {
                    // WebSocketException / InvalidOperationException from a
                    // half-closed socket — ignore, we're tearing down.
                }
            }

            if (_receiveTask != null)
            {
                // Same bound on the receive task: the loop exits on either
                // _cts cancellation or a Close frame, but if neither
                // arrives we don't wait forever.
                var completed = await Task.WhenAny(_receiveTask, Task.Delay(CloseHandshakeTimeout)).ConfigureAwait(false);
                if (completed == _receiveTask)
                {
                    try { await _receiveTask.ConfigureAwait(false); } catch { }
                }
            }

            _ws?.Dispose();
            _cts?.Dispose();
            _sendLock.Dispose();
            if (_ownsHttp) _http.Dispose();
        }
    }
}
#endif
