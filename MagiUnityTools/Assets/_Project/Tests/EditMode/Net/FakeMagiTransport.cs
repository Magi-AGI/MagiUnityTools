using System;
using System.Collections.Concurrent;
using System.Threading;
using System.Threading.Tasks;
using Magi.UnityTools.Net;
using MagiGameServer.Contracts.Core;
using MagiGameServer.Contracts.Protocol;

namespace Magi.UnityTools.Net.Tests
{
    /// In-memory IMagiTransport used by MagiSession EditMode tests. Records
    /// outbound envelopes (so tests can assert what the codec produced)
    /// and exposes PushFrame / PushError to let tests drive the inbound
    /// side. OpenSessionAsync returns a caller-supplied SessionId —
    /// avoids the need for a deterministic GUID generator in tests.
    internal sealed class FakeMagiTransport<TState, TAction> : IMagiTransport<TState, TAction>
    {
        private readonly SessionId _sessionToReturn;
        public ConcurrentQueue<ActionEnvelope<TAction>> SentActions { get; } = new ConcurrentQueue<ActionEnvelope<TAction>>();
        public ConcurrentQueue<TakebackRequest> SentTakebacks { get; } = new ConcurrentQueue<TakebackRequest>();
        public int OpenCalls;
        public int AttachCalls;
        public int DisposeCalls;
        public SessionId AttachedSession;
        public SeatId AttachedSeat;
        /// Set to non-null to make the NEXT AttachAsync throw this exception
        /// (then cleared). Lets a single transport instance exercise both
        /// the failure path and a subsequent successful retry.
        public Exception FailNextAttachWith;
        /// Seat the fake transport hands back from ClaimAndAttachAsync.
        /// Tests that exercise the zero-config path set this before calling
        /// MagiSession.ConnectAsync(config, ct).
        public SeatId ClaimedSeatToReturn = new SeatId(0);

        public event Action<ServerFrame<TState>> OnFrame;
        public event Action<Exception> OnTransportError;

        public FakeMagiTransport(SessionId sessionToReturn)
        {
            _sessionToReturn = sessionToReturn;
        }

        public Task<SessionId> OpenSessionAsync(MagiSessionConfig config, CancellationToken ct)
        {
            Interlocked.Increment(ref OpenCalls);
            return Task.FromResult(_sessionToReturn);
        }

        public Task AttachAsync(SessionId session, SeatId seat, CancellationToken ct)
        {
            Interlocked.Increment(ref AttachCalls);
            var pendingFailure = Interlocked.Exchange(ref FailNextAttachWith, null);
            if (pendingFailure != null) return Task.FromException(pendingFailure);
            AttachedSession = session;
            AttachedSeat = seat;
            return Task.CompletedTask;
        }

        public Task<SeatId> ClaimAndAttachAsync(SessionId session, CancellationToken ct)
        {
            Interlocked.Increment(ref AttachCalls);
            var pendingFailure = Interlocked.Exchange(ref FailNextAttachWith, null);
            if (pendingFailure != null) return Task.FromException<SeatId>(pendingFailure);
            AttachedSession = session;
            AttachedSeat = ClaimedSeatToReturn;
            return Task.FromResult(ClaimedSeatToReturn);
        }

        public string ReattachedToken;

        public Task ReattachAsync(SessionId session, SeatId seat, string reconnectToken, CancellationToken ct)
        {
            Interlocked.Increment(ref AttachCalls);
            var pendingFailure = Interlocked.Exchange(ref FailNextAttachWith, null);
            if (pendingFailure != null) return Task.FromException(pendingFailure);
            AttachedSession = session;
            AttachedSeat = seat;
            ReattachedToken = reconnectToken;
            return Task.CompletedTask;
        }

        public Task SendAsync(ActionEnvelope<TAction> envelope, CancellationToken ct)
        {
            SentActions.Enqueue(envelope);
            return Task.CompletedTask;
        }

        public Task SendAsync(TakebackRequest request, CancellationToken ct)
        {
            SentTakebacks.Enqueue(request);
            return Task.CompletedTask;
        }

        public ValueTask DisposeAsync()
        {
            Interlocked.Increment(ref DisposeCalls);
            return default;
        }

        /// Simulates a server frame arriving on the transport's receive
        /// thread. Invokes OnFrame on the calling thread — tests use this
        /// from background threads to prove MagiSession queues rather
        /// than routing synchronously.
        public void PushFrame(ServerFrame<TState> frame) => OnFrame?.Invoke(frame);

        public void PushError(Exception ex) => OnTransportError?.Invoke(ex);
    }
}
