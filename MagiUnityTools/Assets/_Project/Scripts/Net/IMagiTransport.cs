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

        Task SendAsync(ActionEnvelope<TAction> envelope, CancellationToken ct);
        Task SendAsync(TakebackRequest request, CancellationToken ct);

        event Action<ServerFrame<TState>> OnFrame;
        event Action<Exception> OnTransportError;
    }
}
