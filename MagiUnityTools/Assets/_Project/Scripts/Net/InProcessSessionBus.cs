using System;
using System.Collections.Generic;
using MagiGameServer.Contracts.Core;
using MagiGameServer.Contracts.Protocol;
using MagiGameServer.Core;

namespace Magi.UnityTools.Net
{
    /// Shared fanout hub that lets N InProcessMagiTransport instances drive
    /// a single MagiGameServer.Core.Session in the same process while still
    /// matching the WebSocket broadcast shape: seat 0's action produces an
    /// echo for seat 0 AND a broadcast echo for seat 1, both delivered on
    /// the matching seat's transport. Same semantics the server-side
    /// fanout would produce over the wire, minus the socket hop.
    ///
    /// Generic in TState only — TAction is boxed to object on the way in
    /// (Session.Apply takes ActionEnvelope&lt;object&gt;) and TState is
    /// cast out on the way back when building the typed ServerFrame.
    ///
    /// Not internally synchronised beyond the listener map: Session is not
    /// thread-safe, so callers must serialise Apply/Takeback through a
    /// single driver thread. In a Unity host that's the main thread (every
    /// SendAsync originates from MagiSession.Submit, which is main-thread-only),
    /// so no extra locking is needed in practice.
    public sealed class InProcessSessionBus<TState>
    {
        private readonly Session _session;
        private readonly Dictionary<int, Action<ServerFrame<TState>>> _listeners
            = new Dictionary<int, Action<ServerFrame<TState>>>();
        private readonly object _listenersMutex = new object();

        public InProcessSessionBus(Session session)
        {
            _session = session ?? throw new ArgumentNullException(nameof(session));
        }

        public SessionId SessionId => _session.Id;
        public int SeatCount => _session.SeatCount;

        /// Registers a seat's inbound-frame delivery callback. Rejects a
        /// second attach for a seat that already has a live listener,
        /// mirroring the M4b host's one-live-connection-per-seat rule
        /// (Host/Program.cs closes duplicate sockets with PolicyViolation).
        /// Inventing reconnect/replace semantics here would let client
        /// code pass in-process tests that break on the real host.
        /// Callers must DetachSeat before re-attaching — e.g. after
        /// disposing the old transport.
        public void AttachSeat(SeatId seat, Action<ServerFrame<TState>> deliverer)
        {
            if (deliverer == null) throw new ArgumentNullException(nameof(deliverer));
            if (seat.Value < 0 || seat.Value >= _session.SeatCount)
                throw new ArgumentOutOfRangeException(nameof(seat),
                    $"Seat {seat} out of range [0,{_session.SeatCount}) for session {_session.Id}");
            lock (_listenersMutex)
            {
                if (_listeners.ContainsKey(seat.Value))
                    throw new InvalidOperationException(
                        $"Seat {seat} is already attached to session {_session.Id}; detach the existing listener before re-attaching");
                _listeners[seat.Value] = deliverer;
            }
        }

        public void DetachSeat(SeatId seat)
        {
            lock (_listenersMutex) { _listeners.Remove(seat.Value); }
        }

        /// Builds the JoinSnapshot frame a freshly-attached seat should see.
        /// Lives on the bus (not the transport) so projection and hashing go
        /// through the same Session path that echoes do — keeps the "server
        /// hashes the projected state" invariant in one place.
        public JoinSnapshot<TState> BuildJoinSnapshot(SeatId seat)
        {
            var (projected, hash, rev) = _session.ProjectForSeat(seat);
            return new JoinSnapshot<TState>
            {
                Session = _session.Id,
                ForSeat = seat,
                Revision = rev,
                State = (TState)projected,
                StateHash = hash,
            };
        }

        /// Applies the envelope through the wrapped session and fans every
        /// resulting StateEcho to the matching seat's transport. Includes
        /// Rejected outcomes — rejected echoes still carry the pre-action
        /// canonical state so an optimistic client can roll its local
        /// state back to the server's view.
        public SessionApplyResult Apply(ActionEnvelope<object> envelope)
        {
            if (envelope == null) throw new ArgumentNullException(nameof(envelope));
            var result = _session.Apply(envelope);
            foreach (var echo in result.Echoes)
            {
                Dispatch(echo.ForSeat, new ServerFrame<TState>
                {
                    Kind = ServerFrameKind.StateEcho,
                    Echo = ToTyped(echo),
                });
            }
            return result;
        }

        /// Runs the takeback and delivers the response + broadcast set. The
        /// TakebackResponse only goes to the requester (that's who asked);
        /// TakebackBroadcast goes to every seat when Outcome=Granted (same
        /// as the WS pattern). Denied/PendingConsent produce response-only
        /// — no broadcast because nothing moved.
        public SessionTakebackResult Takeback(TakebackRequest request)
        {
            if (request == null) throw new ArgumentNullException(nameof(request));
            var result = _session.Takeback(request);
            Dispatch(request.RequestingSeat, new ServerFrame<TState>
            {
                Kind = ServerFrameKind.TakebackResponse,
                TakebackResponse = result.Response,
            });
            if (result.Response.Outcome == TakebackOutcome.Granted && result.Echoes != null)
            {
                foreach (var echo in result.Echoes)
                {
                    Dispatch(echo.ForSeat, new ServerFrame<TState>
                    {
                        Kind = ServerFrameKind.TakebackBroadcast,
                        TakebackBroadcast = new TakebackBroadcast<TState>
                        {
                            Session = echo.Session,
                            ForSeat = echo.ForSeat,
                            RequestingSeat = request.RequestingSeat,
                            AckedRequestSeq = request.SeqAtRequestTime,
                            RevisionAfter = echo.Revision,
                            StepsRewound = result.Response.StepsGranted,
                            State = (TState)echo.State,
                            StateHash = echo.StateHash,
                        },
                    });
                }
            }
            return result;
        }

        private static StateEcho<TState> ToTyped(StateEcho<object> echo)
        {
            return new StateEcho<TState>
            {
                Session = echo.Session,
                ForSeat = echo.ForSeat,
                SubmittingSeat = echo.SubmittingSeat,
                AckedSeq = echo.AckedSeq,
                Revision = echo.Revision,
                State = (TState)echo.State,
                StateHash = echo.StateHash,
                Outcome = echo.Outcome,
            };
        }

        private void Dispatch(SeatId seat, ServerFrame<TState> frame)
        {
            Action<ServerFrame<TState>> deliverer;
            lock (_listenersMutex) { _listeners.TryGetValue(seat.Value, out deliverer); }
            deliverer?.Invoke(frame);
        }
    }
}
