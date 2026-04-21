using System;
using System.Threading;
using System.Threading.Tasks;
using MagiGameServer.Contracts.Core;
using MagiGameServer.Contracts.Protocol;

namespace Magi.UnityTools.Net
{
    /// Transport abstraction between MagiSession and the wire. Surfaces
    /// typed ServerFrame&lt;TState&gt; on the inbound side — the codec work
    /// (JSON bytes ↔ ServerFrame) lives inside the concrete transport, so
    /// MagiSession never needs to touch EnvelopeCodec. Tests swap in a
    /// fake transport that invokes OnFrame directly with hand-built
    /// frames, skipping the socket entirely.
    ///
    /// Thread model: OnFrame / OnTransportError may fire on any thread.
    /// MagiSession queues the frames and drains them from Tick() on the
    /// Unity main thread, so the dispatcher (and any event subscribers)
    /// never see a background-thread call.
    public interface IMagiTransport<TState, TAction> : IAsyncDisposable
    {
        /// POST /session/open. Returns the SessionId the server allocated.
        Task<SessionId> OpenSessionAsync(MagiSessionConfig config, CancellationToken ct);

        /// Opens the WebSocket to /session/{id}?seat=N and starts the
        /// background receive loop. First frame delivered via OnFrame is
        /// always the JoinSnapshot the server emits on attach.
        Task AttachAsync(SessionId session, SeatId seat, CancellationToken ct);

        /// Zero-config attach: the server picks the lowest free seat.
        /// Returns the assigned SeatId so the caller can construct a
        /// dispatcher bound to it. Throws if the session is full (server
        /// closes the socket with PolicyViolation "session_full"). The
        /// JoinSnapshot is delivered through OnFrame exactly like the
        /// explicit-seat path — the return value just tells the caller
        /// which seat it landed on.
        Task<SeatId> ClaimAndAttachAsync(SessionId session, CancellationToken ct);

        /// Reattach a previously-claimed seat using the reconnect token
        /// the server returned in the original JoinSnapshot. The server
        /// matches (seat, token) against its ownership map — a mismatch
        /// or unknown seat closes the socket with PolicyViolation
        /// "token_mismatch", and AttachAsync-style callers must fall
        /// back to ClaimAndAttachAsync. On success the JoinSnapshot
        /// arrives through OnFrame carrying the same token, so the
        /// caller's stash stays stable across multiple reconnects.
        Task ReattachAsync(SessionId session, SeatId seat, string reconnectToken, CancellationToken ct);

        Task SendAsync(ActionEnvelope<TAction> envelope, CancellationToken ct);
        Task SendAsync(TakebackRequest request, CancellationToken ct);

        event Action<ServerFrame<TState>> OnFrame;
        event Action<Exception> OnTransportError;
    }
}
