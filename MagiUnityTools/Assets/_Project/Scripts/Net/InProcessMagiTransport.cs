using System;
using System.Threading;
using System.Threading.Tasks;
using MagiGameServer.Contracts.Core;
using MagiGameServer.Contracts.Protocol;

namespace Magi.UnityTools.Net
{
    /// IMagiTransport implementation that drives a MagiGameServer.Core.Session
    /// directly in the client process. Same shape on the MagiSession side as
    /// the WebSocket transport — OpenSessionAsync / AttachAsync / SendAsync /
    /// OnFrame — so the dispatcher, reconcile logic, and game-side observer
    /// wiring never have to know whether the server is remote or local.
    ///
    /// Pairing with a shared InProcessSessionBus is what makes this mirror
    /// the WS broadcast model: every attached seat sees echoes for its own
    /// seat (including broadcast echoes triggered by someone else's action).
    /// One transport per (session, seat), N transports share one bus.
    ///
    /// Lifetime: the transport never owns the Session or the bus. Callers
    /// build both, attach, and dispose the transport when the seat leaves.
    /// Disposing the transport detaches it from the bus but leaves the bus
    /// (and Session) alive for the remaining seats.
    public sealed class InProcessMagiTransport<TState, TAction> : IMagiTransport<TState, TAction>
    {
        private readonly InProcessSessionBus<TState> _bus;
        private SeatId _seat;
        private bool _attached;
        private int _disposed;

        public event Action<ServerFrame<TState>> OnFrame;

        // Required by IMagiTransport but never raised in-process: there is no
        // receive loop that could observe a socket fault, and send failures
        // propagate as faulted Tasks (which MagiSession already queues). The
        // event is exposed so MagiSession's subscribe call compiles without
        // special-casing transport kind.
#pragma warning disable CS0067
        public event Action<Exception> OnTransportError;
#pragma warning restore CS0067

        public InProcessMagiTransport(InProcessSessionBus<TState> bus)
        {
            _bus = bus ?? throw new ArgumentNullException(nameof(bus));
        }

        /// Returns the already-known SessionId of the wrapped bus. The
        /// config is accepted (and ignored) so callers can drive the same
        /// MagiSession.ConnectAsync code path as the WebSocket transport —
        /// session allocation happens outside this transport, usually in a
        /// SessionHost set up by the host-mode driver.
        public Task<SessionId> OpenSessionAsync(MagiSessionConfig config, CancellationToken ct)
        {
            if (Volatile.Read(ref _disposed) != 0)
                throw new ObjectDisposedException(nameof(InProcessMagiTransport<TState, TAction>));
            return Task.FromResult(_bus.SessionId);
        }

        public Task AttachAsync(SessionId session, SeatId seat, CancellationToken ct)
        {
            if (Volatile.Read(ref _disposed) != 0)
                throw new ObjectDisposedException(nameof(InProcessMagiTransport<TState, TAction>));
            if (_attached) throw new InvalidOperationException("AttachAsync already called");
            if (session != _bus.SessionId)
                throw new ArgumentException(
                    $"Attach session {session} does not match bus session {_bus.SessionId}",
                    nameof(session));

            _seat = seat;
            // Subscribe BEFORE firing the JoinSnapshot so any echoes the
            // caller triggers synchronously after attach (e.g. a test that
            // calls SendAsync on the same thread) still land on this seat's
            // listener. Register-then-fire keeps a single ordering rule.
            _bus.AttachSeat(seat, DeliverFrame);
            _attached = true;
            DeliverFrame(new ServerFrame<TState>
            {
                Kind = ServerFrameKind.JoinSnapshot,
                JoinSnapshot = _bus.BuildJoinSnapshot(seat),
            });
            return Task.CompletedTask;
        }

        public Task<SeatId> ClaimAndAttachAsync(SessionId session, CancellationToken ct)
        {
            if (Volatile.Read(ref _disposed) != 0)
                throw new ObjectDisposedException(nameof(InProcessMagiTransport<TState, TAction>));
            if (_attached) throw new InvalidOperationException("AttachAsync already called");
            if (session != _bus.SessionId)
                throw new ArgumentException(
                    $"Attach session {session} does not match bus session {_bus.SessionId}",
                    nameof(session));

            if (!_bus.TryClaimAndAttach(DeliverFrame, out var claimed))
                throw new InvalidOperationException("session_full");

            _seat = claimed;
            _attached = true;
            DeliverFrame(new ServerFrame<TState>
            {
                Kind = ServerFrameKind.JoinSnapshot,
                JoinSnapshot = _bus.BuildJoinSnapshot(claimed),
            });
            return Task.FromResult(claimed);
        }

        /// In-process transport has no socket to drop and nothing to reattach
        /// to — the bus keeps all seats live for the life of the process.
        /// Callers targeting reconnect flows should be on the WebSocket path.
        public Task ReattachAsync(SessionId session, SeatId seat, string reconnectToken, CancellationToken ct)
            => Task.FromException(new NotSupportedException(
                "InProcessMagiTransport does not support reattach — the bus never disconnects seats."));

        public Task SendAsync(ActionEnvelope<TAction> envelope, CancellationToken ct)
        {
            if (Volatile.Read(ref _disposed) != 0)
                throw new ObjectDisposedException(nameof(InProcessMagiTransport<TState, TAction>));
            if (!_attached) throw new InvalidOperationException("AttachAsync must complete before SendAsync");
            if (envelope == null) throw new ArgumentNullException(nameof(envelope));

            // Boxing TAction to object matches what the server-side codec
            // does after JSON deserialization — Session.Apply takes
            // ActionEnvelope<object> and the rules adapter's
            // RulesAdapterBase downcast handles the TAction side. Nothing
            // on the wire here, but we preserve the shape.
            var boxed = new ActionEnvelope<object>
            {
                Session = envelope.Session,
                Seat = envelope.Seat,
                Seq = envelope.Seq,
                Action = envelope.Action,
                PredictedStateHash = envelope.PredictedStateHash,
            };
            // Surface bus.Apply failures only as a faulted Task — MagiSession's
            // fire-and-forget send path already queues task faults via
            // ContinueWith(CaptureSendFault). Firing OnTransportError here as
            // well would deliver the same exception twice through
            // MagiSession.OnTransportError, a shape that the WebSocket
            // transport does not produce and that in-process tests would then
            // lock in as "expected".
            try { _bus.Apply(boxed); }
            catch (Exception ex) { return Task.FromException(ex); }
            return Task.CompletedTask;
        }

        public Task SendAsync(TakebackRequest request, CancellationToken ct)
        {
            if (Volatile.Read(ref _disposed) != 0)
                throw new ObjectDisposedException(nameof(InProcessMagiTransport<TState, TAction>));
            if (!_attached) throw new InvalidOperationException("AttachAsync must complete before SendAsync");
            if (request == null) throw new ArgumentNullException(nameof(request));
            try { _bus.Takeback(request); }
            catch (Exception ex) { return Task.FromException(ex); }
            return Task.CompletedTask;
        }

        public ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0) return default;
            if (_attached) _bus.DetachSeat(_seat);
            return default;
        }

        private void DeliverFrame(ServerFrame<TState> frame) => OnFrame?.Invoke(frame);
    }
}
