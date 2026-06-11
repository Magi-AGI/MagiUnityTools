using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Magi.UnityTools.Net;
using MagiGameServer.Contracts.Core;
using MagiGameServer.Contracts.Protocol;
using MagiGameServer.Contracts.Rules;
using MagiGameServer.Core;
using NUnit.Framework;

namespace Magi.UnityTools.Net.Tests
{
    /// EditMode coverage for the in-process transport. Exercises the same
    /// MagiSession.ConnectAsync / Submit / Tick pipe as the WebSocket
    /// transport — the difference is the Apply runs inline on the caller's
    /// thread instead of round-tripping over a socket. Tests use a minimal
    /// Counter game module (state = int, action = positive delta) so the
    /// asserts focus on transport/bus behaviour rather than Ledge-specific
    /// rules. Multi-seat fanout is the key invariant: seat 0's action must
    /// land as an echo on seat 1's transport just like the WS server does.
    [TestFixture]
    public class InProcessMagiTransportTests
    {
        // Plain { get; set; } rather than { get; init; } so the test assembly
        // doesn't need its own IsExternalInit polyfill — Unity compiles this
        // asmdef without the one that ships internally inside MagiGameServer.Contracts.
        private sealed class CounterState
        {
            public int Value { get; set; }
            public CounterState With(int v) => new CounterState { Value = v };
        }

        private sealed class CounterAction
        {
            public int Delta { get; set; }
        }

        private sealed class CounterRules : RulesAdapterBase<CounterState, CounterAction>
        {
            public override ApplyOutcome Apply(CounterState state, CounterAction action, out CounterState newState)
            {
                if (action.Delta <= 0) { newState = state; return ApplyOutcome.Rejected; }
                newState = state.With(state.Value + action.Delta);
                return ApplyOutcome.Applied;
            }
            public override long GetStateHash(CounterState state) => state.Value;
            public override CounterState ProjectStateFor(CounterState state, SeatId seat) => state;
            public override CounterState SnapshotState(CounterState state) => state;
        }

        private sealed class CounterModule : IGameModule
        {
            public string GameId => "counter";
            public string DisplayName => "Counter (in-proc test)";
            public int MinSeats => 1;
            public int MaxSeats => 4;
            public IRulesAdapter Rules { get; } = new CounterRules();
            public Type ActionType => typeof(CounterAction);
            public Type StateType => typeof(CounterState);
            public object CreateInitialState(GameConfig config) => new CounterState { Value = 0 };
            public object SetSeatPresence(object state, SeatId seat, bool isConnected) => state;
        }

        private static (Session session, InProcessSessionBus<CounterState> bus) NewBus(int seatCount = 2)
        {
            var module = new CounterModule();
            var initial = module.CreateInitialState(new GameConfig { SeatCount = seatCount });
            var session = new Session(new SessionId("in-proc-test"), module, initial, seatCount);
            return (session, new InProcessSessionBus<CounterState>(session));
        }

        private static MagiSessionConfig TestConfig() => new MagiSessionConfig
        {
            BaseUri = "inproc://ignored",
            GameId = "counter",
            SeatCount = 2,
            Options = new Dictionary<string, string>(),
        };

        [Test]
        public async Task ConnectAsync_Drains_JoinSnapshot_WithProjectedStateAndHash()
        {
            var (_, bus) = NewBus();
            var transport = new InProcessMagiTransport<CounterState, CounterAction>(bus);
            var session = new MagiSession<CounterState, CounterAction>(transport);

            JoinSnapshot<CounterState> received = null;
            session.OnSessionJoined += e => received = e;

            await session.ConnectAsync(TestConfig(), new SeatId(0), CancellationToken.None);
            session.Tick();

            Assert.IsNotNull(received, "Attach must emit a JoinSnapshot");
            Assert.AreEqual(bus.SessionId, received.Session);
            Assert.AreEqual(new SeatId(0), received.ForSeat);
            Assert.AreEqual(0, received.State.Value, "initial counter is 0");
            Assert.AreEqual(0L, received.StateHash);
            Assert.AreEqual(0L, received.Revision.Value);
        }

        [Test]
        public async Task Submit_AdvancesStateAndDeliversEchoToAttachedSeat()
        {
            var (_, bus) = NewBus();
            var transport = new InProcessMagiTransport<CounterState, CounterAction>(bus);
            var session = new MagiSession<CounterState, CounterAction>(transport);
            StateEcho<CounterState> matched = null;
            session.OnPredictionMatched += e => matched = e;

            await session.ConnectAsync(TestConfig(), new SeatId(0), CancellationToken.None);
            session.Tick();

            // Predicted hash matches the post-apply server hash (value=3),
            // so the dispatcher retires the pending entry via the matched path.
            session.Submit(new CounterAction { Delta = 3 }, predictedStateHash: 3);
            session.Tick();

            Assert.IsNotNull(matched, "seat 0's own action echoes back as PredictionMatched");
            Assert.AreEqual(ApplyOutcome.Applied, matched.Outcome);
            Assert.AreEqual(3, matched.State.Value);
            Assert.AreEqual(1L, matched.Revision.Value);
            Assert.AreEqual(0, session.Dispatcher.PendingCount);
        }

        [Test]
        public async Task Submit_FanoutEchoesToOtherSeatAsStateAdvanced()
        {
            var (_, bus) = NewBus();
            var transport0 = new InProcessMagiTransport<CounterState, CounterAction>(bus);
            var transport1 = new InProcessMagiTransport<CounterState, CounterAction>(bus);
            var seat0Session = new MagiSession<CounterState, CounterAction>(transport0);
            var seat1Session = new MagiSession<CounterState, CounterAction>(transport1);
            StateEcho<CounterState> seat1Echo = null;
            seat1Session.OnStateAdvanced += e => seat1Echo = e;

            await seat0Session.ConnectAsync(TestConfig(), new SeatId(0), CancellationToken.None);
            await seat1Session.ConnectAsync(TestConfig(), new SeatId(1), CancellationToken.None);
            seat0Session.Tick();
            seat1Session.Tick();

            // Seat 0 acts; seat 1's transport must receive an echo with
            // SubmittingSeat=0, ForSeat=1 — the cross-seat broadcast path
            // that mirrors what the server's WS fanout would emit.
            seat0Session.Submit(new CounterAction { Delta = 5 }, predictedStateHash: 0);
            seat1Session.Tick();

            Assert.IsNotNull(seat1Echo, "seat 1 must see the broadcast echo");
            Assert.AreEqual(new SeatId(0), seat1Echo.SubmittingSeat);
            Assert.AreEqual(new SeatId(1), seat1Echo.ForSeat);
            Assert.AreEqual(5, seat1Echo.State.Value);
            Assert.AreEqual(1L, seat1Echo.Revision.Value);
        }

        [Test]
        public async Task Submit_RejectedAction_EchoesRejectedWithPreActionState()
        {
            var (_, bus) = NewBus();
            var transport = new InProcessMagiTransport<CounterState, CounterAction>(bus);
            var session = new MagiSession<CounterState, CounterAction>(transport);

            // CounterRules rejects non-positive deltas; the dispatcher
            // routes Rejected echoes through OnPredictionDiverged so the
            // local optimistic entry rolls back to the pre-action state.
            StateEcho<CounterState> diverged = null;
            session.OnPredictionDiverged += e => diverged = e;

            await session.ConnectAsync(TestConfig(), new SeatId(0), CancellationToken.None);
            session.Tick();
            session.Submit(new CounterAction { Delta = -1 }, predictedStateHash: 42);
            session.Tick();

            Assert.IsNotNull(diverged);
            Assert.AreEqual(ApplyOutcome.Rejected, diverged.Outcome);
            Assert.AreEqual(0, diverged.State.Value, "rejected echo carries pre-action state");
        }

        [Test]
        public async Task SubmitTakeback_Granted_EchoesBroadcastAndResponseToRequester()
        {
            var (_, bus) = NewBus();
            var transport = new InProcessMagiTransport<CounterState, CounterAction>(bus);
            var session = new MagiSession<CounterState, CounterAction>(transport);

            await session.ConnectAsync(TestConfig(), new SeatId(0), CancellationToken.None);
            session.Tick();
            session.Submit(new CounterAction { Delta = 7 }, predictedStateHash: 0);
            session.Tick();
            Assert.AreEqual(1L, bus.BuildJoinSnapshot(new SeatId(0)).Revision.Value,
                "sanity: session revision advanced to 1 after Submit");

            TakebackResponse reply = null;
            TakebackBroadcast<CounterState> broadcast = null;
            session.OnTakebackReply += r => reply = r;
            session.OnTakebackBroadcast += b => broadcast = b;

            session.SubmitTakeback(stepsRequested: 1, reason: "undo");
            session.Tick();

            Assert.IsNotNull(reply);
            Assert.AreEqual(TakebackOutcome.Granted, reply.Outcome);
            Assert.AreEqual(1, reply.StepsGranted);
            Assert.AreEqual(0L, reply.RevisionAfter.Value, "one-step rewind lands back at revision 0");

            Assert.IsNotNull(broadcast, "granted takeback must also broadcast the post-rewind state");
            Assert.AreEqual(0, broadcast.State.Value);
            Assert.AreEqual(1, broadcast.StepsRewound);
        }

        [Test]
        public async Task SubmitTakeback_NothingToRewind_DeliversDeniedResponse_NoBroadcast()
        {
            var (_, bus) = NewBus();
            var transport = new InProcessMagiTransport<CounterState, CounterAction>(bus);
            var session = new MagiSession<CounterState, CounterAction>(transport);

            await session.ConnectAsync(TestConfig(), new SeatId(0), CancellationToken.None);
            session.Tick();

            TakebackResponse reply = null;
            int broadcasts = 0;
            session.OnTakebackReply += r => reply = r;
            session.OnTakebackBroadcast += _ => broadcasts++;

            session.SubmitTakeback(stepsRequested: 1, reason: "nothing to undo");
            session.Tick();

            Assert.IsNotNull(reply);
            Assert.AreEqual(TakebackOutcome.Denied, reply.Outcome);
            Assert.AreEqual(0, broadcasts, "denied takeback must not broadcast");
        }

        [Test]
        public async Task DisposeAsync_DetachesFromBus_AndStopsDeliveringToSeat()
        {
            var (_, bus) = NewBus();
            var transport0 = new InProcessMagiTransport<CounterState, CounterAction>(bus);
            var transport1 = new InProcessMagiTransport<CounterState, CounterAction>(bus);
            var seat0Session = new MagiSession<CounterState, CounterAction>(transport0);
            var seat1Session = new MagiSession<CounterState, CounterAction>(transport1);
            int seat1Deliveries = 0;
            seat1Session.OnStateAdvanced += _ => seat1Deliveries++;

            await seat0Session.ConnectAsync(TestConfig(), new SeatId(0), CancellationToken.None);
            await seat1Session.ConnectAsync(TestConfig(), new SeatId(1), CancellationToken.None);
            seat0Session.Tick();
            seat1Session.Tick();

            // Dispose seat 1 → transport detaches from the bus → a subsequent
            // seat 0 action should fan an echo only to seat 0, not to a
            // dangling seat 1 listener.
            await seat1Session.DisposeAsync();

            seat0Session.Submit(new CounterAction { Delta = 4 }, predictedStateHash: 0);
            seat1Session.Tick();
            Assert.AreEqual(0, seat1Deliveries, "detached transport must not receive further echoes");
        }

        [Test]
        public void AttachAsync_MismatchedSessionId_Throws()
        {
            var (_, bus) = NewBus();
            var transport = new InProcessMagiTransport<CounterState, CounterAction>(bus);
            Assert.ThrowsAsync<ArgumentException>(async () =>
                await transport.AttachAsync(new SessionId("other"), new SeatId(0), CancellationToken.None));
        }

        [Test]
        public void SendAsync_BeforeAttach_Throws()
        {
            var (_, bus) = NewBus();
            var transport = new InProcessMagiTransport<CounterState, CounterAction>(bus);
            var env = new ActionEnvelope<CounterAction>
            {
                Session = bus.SessionId,
                Seat = new SeatId(0),
                Seq = new ClientSeq(1),
                Action = new CounterAction { Delta = 1 },
            };
            Assert.ThrowsAsync<InvalidOperationException>(async () =>
                await transport.SendAsync(env, CancellationToken.None));
        }

        [Test]
        public async Task AttachAsync_DuplicateSeat_Throws_MirroringHostPolicyViolation()
        {
            // The M4b WS host closes a duplicate seat attach with
            // CloseStatus.PolicyViolation. In-process must refuse the same
            // case — otherwise code that passes local tests would trip on
            // the real host. Detach-then-reattach is the supported path.
            var (_, bus) = NewBus();
            var first = new InProcessMagiTransport<CounterState, CounterAction>(bus);
            var firstSession = new MagiSession<CounterState, CounterAction>(first);
            await firstSession.ConnectAsync(TestConfig(), new SeatId(0), CancellationToken.None);

            var second = new InProcessMagiTransport<CounterState, CounterAction>(bus);
            Assert.ThrowsAsync<InvalidOperationException>(async () =>
                await second.AttachAsync(bus.SessionId, new SeatId(0), CancellationToken.None));

            // After the first transport disposes (which DetachSeat's), a
            // fresh attach on the same seat is allowed again.
            await firstSession.DisposeAsync();
            var third = new InProcessMagiTransport<CounterState, CounterAction>(bus);
            Assert.DoesNotThrowAsync(async () =>
                await third.AttachAsync(bus.SessionId, new SeatId(0), CancellationToken.None));
        }

        [Test]
        public async Task SendAsync_BusThrows_SurfacesAsFaultedTaskOnly_NotOnTransportError()
        {
            // Codex review: OnTransportError must not fire for send faults —
            // MagiSession's fire-and-forget path already queues task faults
            // via ContinueWith, so double-reporting would diverge from the
            // WebSocket transport's single-delivery shape.
            var (_, bus) = NewBus();
            var transport = new InProcessMagiTransport<CounterState, CounterAction>(bus);
            var session = new MagiSession<CounterState, CounterAction>(transport);
            int transportErrors = 0;
            session.OnTransportError += _ => transportErrors++;

            await session.ConnectAsync(TestConfig(), new SeatId(0), CancellationToken.None);
            session.Tick();

            // Envelope with a mismatched session id — Session.Apply throws
            // ArgumentException before touching rules, giving us a
            // deterministic bus-side failure to observe.
            var badEnvelope = new ActionEnvelope<CounterAction>
            {
                Session = new SessionId("not-the-bus"),
                Seat = new SeatId(0),
                Seq = new ClientSeq(1),
                Action = new CounterAction { Delta = 1 },
            };
            var faulted = transport.SendAsync(badEnvelope, CancellationToken.None);
            Assert.IsTrue(faulted.IsFaulted, "send must surface as a faulted Task");
            Assert.IsInstanceOf<ArgumentException>(faulted.Exception?.InnerException);

            // MagiSession never called Submit here, so no fire-and-forget
            // continuation fired. The invariant we care about is: the
            // transport itself did NOT raise OnTransportError for this fault.
            session.Tick();
            Assert.AreEqual(0, transportErrors,
                "InProcessMagiTransport must not raise OnTransportError for send faults");
        }

        // P3 parity: the in-process bus's claim scan picks the lowest free
        // seat just like the server dispatcher. Three transports connect
        // via the zero-config path on a 3-seat bus and must land on
        // 0, 1, 2 in order.
        [Test]
        public async Task ClaimAndAttachAsync_AssignsLowestFreeSeat_InOrder()
        {
            var (_, bus) = NewBus(seatCount: 3);
            var t0 = new InProcessMagiTransport<CounterState, CounterAction>(bus);
            var t1 = new InProcessMagiTransport<CounterState, CounterAction>(bus);
            var t2 = new InProcessMagiTransport<CounterState, CounterAction>(bus);
            var s0 = new MagiSession<CounterState, CounterAction>(t0);
            var s1 = new MagiSession<CounterState, CounterAction>(t1);
            var s2 = new MagiSession<CounterState, CounterAction>(t2);

            await s0.ConnectAsync(TestConfig(), CancellationToken.None);
            await s1.ConnectAsync(TestConfig(), CancellationToken.None);
            await s2.ConnectAsync(TestConfig(), CancellationToken.None);

            Assert.AreEqual(new SeatId(0), s0.Seat);
            Assert.AreEqual(new SeatId(1), s1.Seat);
            Assert.AreEqual(new SeatId(2), s2.Seat);
        }

        // Full session rejects further claims with session_full — symmetric
        // with the server's PolicyViolation close. The client-visible shape
        // is an InvalidOperationException from ClaimAndAttachAsync.
        [Test]
        public async Task ClaimAndAttachAsync_AllSeatsTaken_ThrowsSessionFull()
        {
            var (_, bus) = NewBus(seatCount: 2);
            var t0 = new InProcessMagiTransport<CounterState, CounterAction>(bus);
            var t1 = new InProcessMagiTransport<CounterState, CounterAction>(bus);
            var t2 = new InProcessMagiTransport<CounterState, CounterAction>(bus);
            var s0 = new MagiSession<CounterState, CounterAction>(t0);
            var s1 = new MagiSession<CounterState, CounterAction>(t1);
            var s2 = new MagiSession<CounterState, CounterAction>(t2);

            await s0.ConnectAsync(TestConfig(), CancellationToken.None);
            await s1.ConnectAsync(TestConfig(), CancellationToken.None);
            var ex = Assert.ThrowsAsync<InvalidOperationException>(
                async () => await s2.ConnectAsync(TestConfig(), CancellationToken.None));
            StringAssert.Contains("session_full", ex.Message);
            Assert.IsFalse(s2.IsConnected, "failed claim must not publish the dispatcher");
        }
    }
}
