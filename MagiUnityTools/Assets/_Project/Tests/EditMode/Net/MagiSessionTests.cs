using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Magi.UnityTools.Net;
using MagiGameServer.Codec;
using MagiGameServer.Contracts.Core;
using MagiGameServer.Contracts.Protocol;
using NUnit.Framework;

namespace Magi.UnityTools.Net.Tests
{
    /// EditMode coverage for the Unity-side shim. Every test drives
    /// MagiSession through a FakeMagiTransport so the socket layer is
    /// out of scope — the real transport is validated end-to-end in M6
    /// once the LedgeBoardGame scene wires up a live server.
    [TestFixture]
    public class MagiSessionTests
    {
        private const string TestSessionValue = "test-session";

        private static SessionId TestSession() => new SessionId(TestSessionValue);

        private static MagiSessionConfig TestConfig() => new MagiSessionConfig
        {
            BaseUri = "http://localhost:12345",
            GameId = "counter",
            SeatCount = 2,
            Seed = 0,
            Options = new Dictionary<string, string>(),
        };

        [Test]
        public async Task ConnectAsync_Opens_Then_Attaches_And_RecordsSessionAndSeat()
        {
            var transport = new FakeMagiTransport<string, string>(TestSession());
            var session = new MagiSession<string, string>(transport);

            await session.ConnectAsync(TestConfig(), new SeatId(1), CancellationToken.None);

            Assert.IsTrue(session.IsConnected, "IsConnected should flip after ConnectAsync");
            Assert.AreEqual(1, transport.OpenCalls);
            Assert.AreEqual(1, transport.AttachCalls);
            Assert.AreEqual(TestSession(), transport.AttachedSession);
            Assert.AreEqual(new SeatId(1), transport.AttachedSeat);
            Assert.AreEqual(TestSession(), session.Session);
            Assert.AreEqual(new SeatId(1), session.Seat);
        }

        [Test]
        public async Task Tick_Drains_JoinSnapshot_Into_OnSessionJoined()
        {
            var (session, transport) = await NewConnectedSession();
            JoinSnapshot<string> received = null;
            session.OnSessionJoined += s => received = s;

            transport.PushFrame(new ServerFrame<string>
            {
                Kind = ServerFrameKind.JoinSnapshot,
                JoinSnapshot = new JoinSnapshot<string>
                {
                    Session = session.Session,
                    ForSeat = session.Seat,
                    Revision = new ServerSeq(0),
                    State = "initial",
                    StateHash = 42,
                },
            });

            Assert.IsNull(received, "Frame must not route synchronously — only Tick should drain");
            int drained = session.Tick();
            Assert.AreEqual(1, drained);
            Assert.IsNotNull(received);
            Assert.AreEqual("initial", received.State);
            Assert.AreEqual(42L, received.StateHash);
        }

        [Test]
        public async Task Tick_Routes_StateEcho_ToPredictionMatched_WhenOwnSeqMatches()
        {
            var (session, transport) = await NewConnectedSession();
            StateEcho<string> matched = null;
            session.OnPredictionMatched += e => matched = e;
            session.OnStateAdvanced += e => Assert.Fail("OnStateAdvanced should not fire for our own matched echo");

            // Submit pushes a pending entry with ClientSeq=1 and
            // predictedStateHash=99; the server echoes back Applied with
            // the matching hash.
            session.Submit("act", predictedStateHash: 99);

            transport.PushFrame(new ServerFrame<string>
            {
                Kind = ServerFrameKind.StateEcho,
                Echo = new StateEcho<string>
                {
                    Session = session.Session,
                    ForSeat = session.Seat,
                    SubmittingSeat = session.Seat,
                    AckedSeq = new ClientSeq(1),
                    Revision = new ServerSeq(1),
                    State = "after",
                    StateHash = 99,
                    Outcome = ApplyOutcome.Applied,
                },
            });

            session.Tick();
            Assert.IsNotNull(matched);
            Assert.AreEqual(0, session.Dispatcher.PendingCount, "Matching echo retires the optimistic entry");
        }

        [Test]
        public async Task Tick_Routes_StateEcho_ToStateAdvanced_WhenRemoteSubmission()
        {
            var (session, transport) = await NewConnectedSession();
            StateEcho<string> advanced = null;
            session.OnStateAdvanced += e => advanced = e;

            transport.PushFrame(new ServerFrame<string>
            {
                Kind = ServerFrameKind.StateEcho,
                Echo = new StateEcho<string>
                {
                    Session = session.Session,
                    ForSeat = session.Seat,
                    SubmittingSeat = new SeatId(0),
                    AckedSeq = new ClientSeq(5),
                    Revision = new ServerSeq(7),
                    State = "remote",
                    StateHash = 7,
                    Outcome = ApplyOutcome.Applied,
                },
            });

            session.Tick();
            Assert.IsNotNull(advanced);
            Assert.AreEqual("remote", advanced.State);
        }

        [Test]
        public async Task Tick_Routes_TakebackBroadcast_ToOnTakebackBroadcast()
        {
            var (session, transport) = await NewConnectedSession();
            TakebackBroadcast<string> broadcast = null;
            session.OnTakebackBroadcast += b => broadcast = b;

            transport.PushFrame(new ServerFrame<string>
            {
                Kind = ServerFrameKind.TakebackBroadcast,
                TakebackBroadcast = new TakebackBroadcast<string>
                {
                    Session = session.Session,
                    ForSeat = session.Seat,
                    RequestingSeat = new SeatId(0),
                    AckedRequestSeq = new ClientSeq(3),
                    RevisionAfter = new ServerSeq(4),
                    StepsRewound = 2,
                    State = "rewound",
                    StateHash = 11,
                },
            });

            session.Tick();
            Assert.IsNotNull(broadcast);
            Assert.AreEqual(2, broadcast.StepsRewound);
        }

        [Test]
        public async Task Tick_Routes_TakebackResponse_ToOnTakebackReply()
        {
            var (session, transport) = await NewConnectedSession();
            TakebackResponse received = null;
            session.OnTakebackReply += r => received = r;

            transport.PushFrame(new ServerFrame<string>
            {
                Kind = ServerFrameKind.TakebackResponse,
                TakebackResponse = new TakebackResponse
                {
                    Session = session.Session,
                    RequestingSeat = session.Seat,
                    AckedRequestSeq = new ClientSeq(4),
                    Outcome = TakebackOutcome.Denied,
                    Message = "not_allowed",
                },
            });

            session.Tick();
            Assert.IsNotNull(received);
            Assert.AreEqual(TakebackOutcome.Denied, received.Outcome);
            Assert.AreEqual("not_allowed", received.Message);
        }

        [Test]
        public async Task Tick_Routes_ErrorEnvelope_ToOnError_And_RetiresPendingPrediction()
        {
            var (session, transport) = await NewConnectedSession();
            ErrorEnvelope received = null;
            session.OnError += e => received = e;

            // Submit pushes pending seq=1; server rejects with an error
            // whose AckedSeq=1. The dispatcher must clear the pending
            // entry so local undo isn't blocked by a ghost prediction.
            session.Submit("act", predictedStateHash: 1);
            Assert.AreEqual(1, session.Dispatcher.PendingCount);

            transport.PushFrame(new ServerFrame<string>
            {
                Kind = ServerFrameKind.Error,
                Error = new ErrorEnvelope
                {
                    Session = session.Session,
                    AckedSeq = new ClientSeq(1),
                    Code = "rejected",
                    Message = "nope",
                },
            });

            session.Tick();
            Assert.IsNotNull(received);
            Assert.AreEqual("rejected", received.Code);
            Assert.AreEqual(0, session.Dispatcher.PendingCount, "Matching error retires the optimistic entry");
        }

        [Test]
        public async Task Submit_Sends_ActionEnvelope_WithCodecWireShape()
        {
            var (session, transport) = await NewConnectedSession();

            session.Submit("counter.increment", predictedStateHash: 17);

            Assert.AreEqual(1, transport.SentActions.Count);
            Assert.IsTrue(transport.SentActions.TryPeek(out var envelope));
            Assert.AreEqual(session.Session, envelope.Session);
            Assert.AreEqual(session.Seat, envelope.Seat);
            Assert.AreEqual(new ClientSeq(1), envelope.Seq);
            Assert.AreEqual("counter.increment", envelope.Action);
            Assert.AreEqual(17L, envelope.PredictedStateHash);

            // Roundtrip the envelope through the codec to confirm the wire
            // shape (camelCase, strong-id converters) is consistent with
            // what the server reads on the other side.
            var bytes = EnvelopeCodec.Serialize(envelope);
            using var doc = JsonDocument.Parse(bytes);
            Assert.AreEqual(TestSessionValue, doc.RootElement.GetProperty("session").GetString());
            Assert.AreEqual(1, doc.RootElement.GetProperty("seat").GetInt32());
            Assert.AreEqual(1, doc.RootElement.GetProperty("seq").GetInt64());
            Assert.AreEqual("counter.increment", doc.RootElement.GetProperty("action").GetString());
            Assert.AreEqual(17, doc.RootElement.GetProperty("predictedStateHash").GetInt64());
        }

        [Test]
        public async Task SubmitTakeback_Sends_TakebackRequest_WithSeqAtRequestTime()
        {
            var (session, transport) = await NewConnectedSession();

            session.SubmitTakeback(stepsRequested: 2, reason: "misclick");

            Assert.AreEqual(1, transport.SentTakebacks.Count);
            Assert.IsTrue(transport.SentTakebacks.TryPeek(out var req));
            Assert.AreEqual(session.Session, req.Session);
            Assert.AreEqual(session.Seat, req.RequestingSeat);
            Assert.AreEqual(new ClientSeq(1), req.SeqAtRequestTime);
            Assert.AreEqual(2, req.StepsRequested);
            Assert.AreEqual("misclick", req.Reason);
        }

        [Test]
        public async Task Tick_DrainsFrames_PushedFromBackgroundThread()
        {
            var (session, transport) = await NewConnectedSession();
            int deliveries = 0;
            session.OnStateAdvanced += _ => deliveries++;

            // Fire 16 frames from a ThreadPool worker; Tick on this thread
            // should drain all of them. This proves the Concurrent queue
            // + Tick pump decouples the transport thread from subscribers.
            var pushed = 16;
            await Task.Run(() =>
            {
                for (int i = 0; i < pushed; i++)
                {
                    transport.PushFrame(new ServerFrame<string>
                    {
                        Kind = ServerFrameKind.StateEcho,
                        Echo = new StateEcho<string>
                        {
                            Session = session.Session,
                            ForSeat = session.Seat,
                            SubmittingSeat = new SeatId(0),
                            AckedSeq = new ClientSeq(i + 1),
                            Revision = new ServerSeq(i + 1),
                            State = "s" + i,
                            StateHash = i,
                            Outcome = ApplyOutcome.Applied,
                        },
                    });
                }
            });

            Assert.AreEqual(0, deliveries, "Nothing delivered until Tick");
            int drained = session.Tick();
            Assert.AreEqual(pushed, drained);
            Assert.AreEqual(pushed, deliveries);
            Assert.AreEqual(0, session.Tick(), "Second Tick has nothing to drain");
        }

        [Test]
        public async Task Tick_Drains_TransportErrors_To_OnTransportError()
        {
            var (session, transport) = await NewConnectedSession();
            Exception captured = null;
            session.OnTransportError += ex => captured = ex;

            var boom = new InvalidOperationException("boom");
            transport.PushError(boom);

            Assert.IsNull(captured, "Transport errors must queue, not route synchronously");
            session.Tick();
            Assert.AreSame(boom, captured);
        }

        [Test]
        public async Task Submit_BeforeConnect_Throws()
        {
            var transport = new FakeMagiTransport<string, string>(TestSession());
            var session = new MagiSession<string, string>(transport);
            Assert.Throws<InvalidOperationException>(() => session.Submit("x", 0));
            await session.DisposeAsync();
        }

        [Test]
        public void ConnectAsync_AttachFailure_LeavesSessionUnconnected()
        {
            var transport = new FakeMagiTransport<string, string>(TestSession());
            var session = new MagiSession<string, string>(transport);
            var boom = new InvalidOperationException("attach refused");
            transport.FailNextAttachWith = boom;

            var ex = Assert.ThrowsAsync<InvalidOperationException>(
                async () => await session.ConnectAsync(TestConfig(), new SeatId(1), CancellationToken.None));
            Assert.AreSame(boom, ex);
            Assert.IsFalse(session.IsConnected, "Half-connected state must not leak IsConnected=true");
            Assert.IsNull(session.Dispatcher, "Dispatcher must not be published on attach failure");
            Assert.Throws<InvalidOperationException>(() => session.Submit("x", 0),
                "Submit must stay blocked — the transport never attached");
        }

        [Test]
        public async Task ConnectAsync_AfterAttachFailure_RetrySucceeds()
        {
            var transport = new FakeMagiTransport<string, string>(TestSession());
            var session = new MagiSession<string, string>(transport);
            transport.FailNextAttachWith = new InvalidOperationException("first try");

            Assert.ThrowsAsync<InvalidOperationException>(
                async () => await session.ConnectAsync(TestConfig(), new SeatId(1), CancellationToken.None));

            // Retry with a fresh attach — must not trip the "already connected" guard.
            await session.ConnectAsync(TestConfig(), new SeatId(1), CancellationToken.None);
            Assert.IsTrue(session.IsConnected);
            Assert.AreEqual(2, transport.AttachCalls);
            await session.DisposeAsync();
        }

        [Test]
        public async Task DisposeAsync_DisposesTransport()
        {
            var (session, transport) = await NewConnectedSession();
            await session.DisposeAsync();
            Assert.AreEqual(1, transport.DisposeCalls);
            // Idempotent — second call is a no-op.
            await session.DisposeAsync();
            Assert.AreEqual(1, transport.DisposeCalls);
        }

        private static async Task<(MagiSession<string, string> session, FakeMagiTransport<string, string> transport)> NewConnectedSession()
        {
            var transport = new FakeMagiTransport<string, string>(TestSession());
            var session = new MagiSession<string, string>(transport);
            await session.ConnectAsync(TestConfig(), new SeatId(1), CancellationToken.None);
            return (session, transport);
        }
    }
}
