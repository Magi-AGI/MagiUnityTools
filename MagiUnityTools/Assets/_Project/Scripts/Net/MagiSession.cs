using System;
using System.Collections.Concurrent;
using System.Threading;
using System.Threading.Tasks;
using MagiGameServer.Client;
using MagiGameServer.Contracts.Core;
using MagiGameServer.Contracts.Protocol;

namespace Magi.UnityTools.Net
{
    /// Unity-facing wrapper around SessionDispatcher. Does three things the
    /// pure-.NET dispatcher can't do on its own:
    ///
    /// * Moves inbound frames from whatever thread the transport delivers
    ///   them on (ClientWebSocket's receive loop runs on a ThreadPool
    ///   thread) to the Unity main thread, so the dispatcher's optimistic
    ///   stack — a plain List&lt;T&gt; — stays single-threaded and
    ///   event subscribers can safely touch Unity APIs.
    /// * Wires outbound dispatcher events (OutgoingAction / OutgoingTakeback)
    ///   to the transport's SendAsync surface with fire-and-forget plus
    ///   error capture.
    /// * Owns the transport lifecycle so a caller just sees ConnectAsync /
    ///   CloseAsync.
    ///
    /// Marshalling is explicit: Tick() must be called on the main thread
    /// each frame (e.g. from a MonoBehaviour's Update). Not using
    /// SynchronizationContext.Post because a queue+pump is easier to test,
    /// doesn't depend on Unity's SynchronizationContext being captured at
    /// the right moment, and keeps all dispatcher calls on a thread we
    /// chose rather than whichever one the transport's continuation landed
    /// on.
    ///
    /// A MonoBehaviour driver that pumps Tick() automatically is deferred
    /// to M6 where the LedgeBoardGame scene will wire one up as part of
    /// the adopter work.
    public sealed class MagiSession<TState, TAction> : IAsyncDisposable
    {
        private readonly IMagiTransport<TState, TAction> _transport;
        private readonly ConcurrentQueue<ServerFrame<TState>> _inbound = new ConcurrentQueue<ServerFrame<TState>>();
        private readonly ConcurrentQueue<Exception> _errors = new ConcurrentQueue<Exception>();
        private SessionDispatcher<TState, TAction> _dispatcher;
        private SessionId _session;
        private SeatId _seat;
        private int _disposed;

        public MagiSession(IMagiTransport<TState, TAction> transport)
        {
            _transport = transport ?? throw new ArgumentNullException(nameof(transport));
            _transport.OnFrame += OnTransportFrame;
            _transport.OnTransportError += OnTransportErrorRaised;
        }

        /// Set once ConnectAsync completes. Exposed mainly for tests and
        /// diagnostics — game code should react to OnSessionJoined rather
        /// than poll IsConnected.
        public bool IsConnected => _dispatcher != null;
        public SessionId Session => _session;
        public SeatId Seat => _seat;
        public SessionDispatcher<TState, TAction> Dispatcher => _dispatcher;

        // Passthrough observer surface. Subscribers fire on the thread that
        // called Tick() — i.e. the main thread — because all dispatcher
        // Ingest calls happen inside Tick.
        public event Action<JoinSnapshot<TState>> OnSessionJoined;
        public event Action<StateEcho<TState>> OnStateAdvanced;
        public event Action<StateEcho<TState>> OnPredictionMatched;
        public event Action<StateEcho<TState>> OnPredictionDiverged;
        public event Action<TakebackBroadcast<TState>> OnTakebackBroadcast;
        public event Action<TakebackResponse> OnTakebackReply;
        public event Action<ErrorEnvelope> OnError;
        public event Action<Exception> OnTransportError;

        /// Opens a session over HTTP and attaches the WebSocket. Completes
        /// once the socket is open — the first server frame (JoinSnapshot)
        /// is still pending and will flow through OnSessionJoined after
        /// the next Tick() on the main thread. That means OnSessionJoined,
        /// not ConnectAsync's completion, is the authoritative "session
        /// is ready for game actions" signal.
        public async Task ConnectAsync(MagiSessionConfig config, SeatId seat, CancellationToken ct)
        {
            if (config == null) throw new ArgumentNullException(nameof(config));
            if (_dispatcher != null) throw new InvalidOperationException("Session already connected");

            _session = await _transport.OpenSessionAsync(config, ct).ConfigureAwait(false);
            _seat = seat;
            _dispatcher = new SessionDispatcher<TState, TAction>(_session, seat);
            _dispatcher.OnSessionJoined += e => OnSessionJoined?.Invoke(e);
            _dispatcher.OnStateAdvanced += e => OnStateAdvanced?.Invoke(e);
            _dispatcher.OnPredictionMatched += e => OnPredictionMatched?.Invoke(e);
            _dispatcher.OnPredictionDiverged += e => OnPredictionDiverged?.Invoke(e);
            _dispatcher.OnTakebackBroadcast += e => OnTakebackBroadcast?.Invoke(e);
            _dispatcher.OnTakebackReply += e => OnTakebackReply?.Invoke(e);
            _dispatcher.OnError += e => OnError?.Invoke(e);
            _dispatcher.OutgoingAction += SendActionFireAndForget;
            _dispatcher.OutgoingTakeback += SendTakebackFireAndForget;

            await _transport.AttachAsync(_session, seat, ct).ConfigureAwait(false);
        }

        public void Submit(TAction action, long predictedStateHash)
        {
            if (_dispatcher == null) throw new InvalidOperationException("ConnectAsync must complete before Submit");
            _dispatcher.Submit(action, predictedStateHash);
        }

        public void SubmitTakeback(int stepsRequested, string reason)
        {
            if (_dispatcher == null) throw new InvalidOperationException("ConnectAsync must complete before SubmitTakeback");
            _dispatcher.SubmitTakeback(stepsRequested, reason);
        }

        /// Drains the inbound queue into the dispatcher on the caller's
        /// thread. Returns the number of frames delivered, which tests
        /// assert against and UIs can use as a liveness signal. Safe to
        /// call before ConnectAsync completes — errors still drain, and
        /// frames wait (they arrive only after AttachAsync anyway).
        public int Tick()
        {
            while (_errors.TryDequeue(out var err))
            {
                try { OnTransportError?.Invoke(err); } catch { }
            }
            if (_dispatcher == null) return 0;
            int drained = 0;
            while (_inbound.TryDequeue(out var frame))
            {
                Route(frame);
                drained++;
            }
            return drained;
        }

        private void Route(ServerFrame<TState> frame)
        {
            switch (frame.Kind)
            {
                case ServerFrameKind.JoinSnapshot:
                    if (frame.JoinSnapshot != null) _dispatcher.Ingest(frame.JoinSnapshot);
                    break;
                case ServerFrameKind.StateEcho:
                    if (frame.Echo != null) _dispatcher.Ingest(frame.Echo);
                    break;
                case ServerFrameKind.TakebackBroadcast:
                    if (frame.TakebackBroadcast != null) _dispatcher.Ingest(frame.TakebackBroadcast);
                    break;
                case ServerFrameKind.TakebackResponse:
                    if (frame.TakebackResponse != null) _dispatcher.Ingest(frame.TakebackResponse);
                    break;
                case ServerFrameKind.Error:
                    if (frame.Error != null) _dispatcher.Ingest(frame.Error);
                    break;
            }
        }

        private void OnTransportFrame(ServerFrame<TState> frame)
        {
            if (frame != null) _inbound.Enqueue(frame);
        }

        private void OnTransportErrorRaised(Exception ex)
        {
            if (ex != null) _errors.Enqueue(ex);
        }

        private void SendActionFireAndForget(ActionEnvelope<TAction> envelope)
        {
            Task task;
            try { task = _transport.SendAsync(envelope, CancellationToken.None); }
            catch (Exception ex) { _errors.Enqueue(ex); return; }
            if (task != null) task.ContinueWith(CaptureSendFault, TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously);
        }

        private void SendTakebackFireAndForget(TakebackRequest request)
        {
            Task task;
            try { task = _transport.SendAsync(request, CancellationToken.None); }
            catch (Exception ex) { _errors.Enqueue(ex); return; }
            if (task != null) task.ContinueWith(CaptureSendFault, TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously);
        }

        private void CaptureSendFault(Task faulted)
        {
            if (faulted.Exception != null)
            {
                foreach (var inner in faulted.Exception.Flatten().InnerExceptions)
                    _errors.Enqueue(inner);
            }
        }

        public async ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
            _transport.OnFrame -= OnTransportFrame;
            _transport.OnTransportError -= OnTransportErrorRaised;
            await _transport.DisposeAsync().ConfigureAwait(false);
        }
    }
}
