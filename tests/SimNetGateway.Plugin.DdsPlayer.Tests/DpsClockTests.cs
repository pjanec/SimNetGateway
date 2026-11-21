using System.Diagnostics;
using CycloneDDS.Runtime;
using SimNetGateway.Gateway.HrotDis;
using SimNetGateway.Protocol.Hrot;
using D = SimNetGateway.Protocol.DdsPlayer;
using Rig = SimNetGateway.Plugin.DdsPlayer.Tests.DpsOutboundTests.Rig;
using Watch = SimNetGateway.Plugin.DdsPlayer.Tests.DpsOutboundTests;

namespace SimNetGateway.Plugin.DdsPlayer.Tests;

/// <summary>DPS round 7, parts A-C in the plugin (DD-022 3b, 3c, 3g): the clock and step topics, the probe echo, the tick arithmetic, and the clients' world readiness behind the host's gate.</summary>
public class DpsClockTests
{
    private const long BaseTicks = 639_000_000_000_000_000L;   // about 6.4e17, like a real tick

    // ---- the clock and step topics -----------------------------------------------------------------------------------------------

    [Fact]
    public void An_anchor_is_one_sim_clock_sample_with_a_growing_seq_and_a_step_is_a_clock_step_plus_a_stepped_sim_clock_never_acknowledged()
    {
        uint domain = Watch.NewDomain();
        using var rig = new Rig(domain);
        var clocks = new List<D.SimClock>();
        var steps = new List<D.ClockStep>();
        rig.Transport.SimClock.Tap = clocks.Add;
        rig.Transport.ClockStep.Tap = steps.Add;

        rig.Send(new HrotClockAnchor(false, BaseTicks + 5, 12.5, 2f, 0.05f, 3));
        rig.Send(new HrotClockAnchor(true, BaseTicks + 9, 13.0, 2f, 0.05f, 3));
        rig.Send(new HrotStep(501, 0.05f, 13.1, 2f, 3));

        Assert.Equal(3, clocks.Count);
        Assert.Equal((D.ClockMode.ClockModeContinuous, BaseTicks + 5, 12.5, 2f, 0.05f, 0L, 3L, 1u),
            (clocks[0].Mode, clocks[0].AnchorWallTicks, clocks[0].AnchorSimTime, clocks[0].TimeScale, clocks[0].FixedDelta, clocks[0].StepSeq, clocks[0].TimelineEpoch, clocks[0].Seq));
        Assert.Equal((D.ClockMode.ClockModeStepped, 13.0, 2u), (clocks[1].Mode, clocks[1].AnchorSimTime, clocks[1].Seq));

        // the step: a stepped clock at its target, and the step itself, acknowledgement not asked for
        Assert.Equal((D.ClockMode.ClockModeStepped, 13.1, 501L, 3u), (clocks[2].Mode, clocks[2].AnchorSimTime, clocks[2].StepSeq, clocks[2].Seq));
        var step = Assert.Single(steps);
        Assert.Equal((501L, 0.05f, 13.1, 2f, 3L, false, 0u), (step.StepSeq, step.FixedDelta, step.TargetSimTime, step.TimeScale, step.TimelineEpoch, step.AckRequired, step.Samples));
    }

    [Fact]
    public void A_non_finite_anchor_or_step_is_dropped()
    {
        uint domain = Watch.NewDomain();
        using var rig = new Rig(domain);
        var clocks = new List<D.SimClock>();
        rig.Transport.SimClock.Tap = clocks.Add;

        rig.Send(new HrotClockAnchor(false, BaseTicks, double.NaN, 1f, 0f, 0));
        rig.Send(new HrotStep(1, 0.05f, double.PositiveInfinity, 1f, 0));

        Assert.Empty(clocks);
    }

    // ---- the tick arithmetic ------------------------------------------------------------------------------------------------------

    [Fact]
    public void Elapsed_time_subtracts_the_tick_values_before_converting_to_double()
    {
        // two ticks about 6.4e17 apart by one tick: a double resolves only 128 ticks there, so (double)now - (double)anchor would give 0 or 128 ticks
        long anchor = BaseTicks + 1;
        Assert.Equal(1e-7, DpsClockFormula.SecondsSince(anchor, anchor + 1), 15);
        Assert.Equal(3.0000001, DpsClockFormula.SecondsSince(anchor, anchor + 30_000_001), 12);
        var clock = new D.SimClock { Mode = D.ClockMode.ClockModeContinuous, AnchorWallTicks = anchor, AnchorSimTime = 10.0, TimeScale = 2f };
        Assert.Equal(10.0 + 2e-7, DpsClockFormula.SimTime(in clock, anchor + 1), 12);
        Assert.Equal(10.0, DpsClockFormula.SimTime(clock with { Mode = D.ClockMode.ClockModeStepped }, anchor + 5_000_000), 12);
    }

    // ---- the probe echo -----------------------------------------------------------------------------------------------------------

    /// <summary>A station clock for the tests: a base plus a stopwatch, like the host's.</summary>
    private sealed class StationClock
    {
        private readonly Stopwatch _sw = Stopwatch.StartNew();

        public long Ticks() => BaseTicks + _sw.Elapsed.Ticks;
    }

    /// <summary>A client of the clock handshake with its own tick = the station's + <paramref name="offsetTicks"/>, probing and reading echoes on its own thread of control (the test's).</summary>
    private sealed class ProbeClient : IDisposable
    {
        private readonly DdsParticipant _participant;
        private readonly DdsWriter<D.ClockProbe> _probes;
        private readonly DdsReader<D.ClockEcho> _echoes;
        private readonly StationClock _station;
        private readonly long _offset;

        public ProbeClient(uint domain, string partition, StationClock station, long offsetTicks, ulong clientId)
        {
            _participant = new DdsParticipant(domain, defaultPartition: partition);
            _probes = new DdsWriter<D.ClockProbe>(_participant);
            _echoes = new DdsReader<D.ClockEcho>(_participant);
            _station = station;
            _offset = offsetTicks;
            ClientId = clientId;
        }

        public ulong ClientId { get; }

        public List<D.ClockEcho> Received { get; } = new();

        public long Now() => _station.Ticks() + _offset;

        /// <summary>One probe, until its echo arrives (the first probes may find no matched reader yet); returns the NTP offset (station minus client) in ticks.</summary>
        public long? Probe(int timeoutMs = 6000)
        {
            var sw = Stopwatch.StartNew();
            while (sw.ElapsedMilliseconds < timeoutMs)
            {
                long t0 = Now();
                _probes.Write(new D.ClockProbe { ClientId = ClientId, ClientSendTicks = t0 });
                var give = Stopwatch.StartNew();
                while (give.ElapsedMilliseconds < 200)
                {
                    using var loan = _echoes.Take(16);
                    foreach (var sample in loan)
                    {
                        if (!sample.IsValid)
                        {
                            continue;
                        }

                        long t3 = Now();
                        var echo = sample.Data;
                        Received.Add(echo);
                        if (echo.ClientId == ClientId && echo.ClientSendTicks == t0)
                        {
                            return ((echo.StationReceiveTicks - t0) + (echo.StationTransmitTicks - t3)) / 2;
                        }
                    }

                    Thread.Sleep(1);
                }
            }

            return null;
        }

        public void Dispose()
        {
            _echoes.Dispose();
            _probes.Dispose();
            _participant.Dispose();
        }
    }

    private static long Median(IEnumerable<long> values)
    {
        var sorted = values.OrderBy(v => v).ToArray();
        return sorted[sorted.Length / 2];
    }

    private const long OneMillisecond = 10_000;

    [Fact]
    public void The_echo_recovers_a_known_clock_offset_within_a_millisecond_with_no_host_frame_running()
    {
        uint domain = Watch.NewDomain();
        var station = new StationClock();
        using var rig = new Rig(domain, stationTicks: station.Ticks);   // the rig's frame is never ticked: the echo is answered on its own thread
        using var client = new ProbeClient(domain, "station-1", station, offsetTicks: 30_000_000, clientId: 7);

        var offsets = new List<long>();
        for (int i = 0; i < 12; i++)
        {
            offsets.Add(client.Probe() ?? throw new Xunit.Sdk.XunitException("no echo"));
            Thread.Sleep(5);
        }

        // station = client - 3 s: the client's clock is 3 s ahead
        Assert.InRange(Median(offsets) + 30_000_000, -OneMillisecond, OneMillisecond);
        Assert.True(rig.Transport.ProbesAnswered >= 12);
        Assert.All(client.Received, e => Assert.True(e.StationReceiveTicks <= e.StationTransmitTicks));
    }

    [Fact]
    public void A_slow_reader_does_not_move_the_receive_stamp()
    {
        // a reader that takes a while after the probe arrived (the frame-bound reader of the first design) must not make the outbound leg look longer: the stamp is taken when the wait returns
        uint domain = Watch.NewDomain();
        var station = new StationClock();
        using var participant = new DdsParticipant(domain, defaultPartition: "echo-delay");
        using var echo = new DpsClockEcho(participant, station.Ticks, _ => { }, afterWait: () => Thread.Sleep(30));
        using var client = new ProbeClient(domain, "echo-delay", station, offsetTicks: -20_000_000, clientId: 8);

        var offsets = new List<long>();
        for (int i = 0; i < 8; i++)
        {
            offsets.Add(client.Probe() ?? throw new Xunit.Sdk.XunitException("no echo"));
        }

        Assert.InRange(Median(offsets) - 20_000_000, -OneMillisecond, OneMillisecond);
    }

    [Fact]
    public void An_echo_carries_the_id_and_send_time_of_the_probe_it_answers()
    {
        uint domain = Watch.NewDomain();
        var station = new StationClock();
        using var rig = new Rig(domain, stationTicks: station.Ticks);
        using var a = new ProbeClient(domain, "station-1", station, 0, clientId: 11);
        using var b = new ProbeClient(domain, "station-1", station, 5_000_000, clientId: 22);

        Assert.NotNull(a.Probe());
        Assert.NotNull(b.Probe());
        for (int i = 0; i < 3; i++)
        {
            a.Probe();
            b.Probe();
        }

        // every echo on the wire answers a probe of its own client (the echo is keyed by ClientId, a client takes its own): each client's own echoes are matched by id and send time
        Assert.Contains(a.Received, e => e.ClientId == 11);
        Assert.Contains(b.Received, e => e.ClientId == 22);
        Assert.All(a.Received.Concat(b.Received), e => Assert.True(e.ClientId is 11 or 22));
        Assert.All(a.Received.Where(e => e.ClientId == 11), e => Assert.InRange(e.ClientSendTicks, BaseTicks, BaseTicks + TimeSpan.FromMinutes(10).Ticks));
        Assert.All(b.Received.Where(e => e.ClientId == 22), e => Assert.InRange(e.ClientSendTicks - 5_000_000, BaseTicks, BaseTicks + TimeSpan.FromMinutes(10).Ticks));   // B's send ticks are B's own clock
    }

    // ---- the clients' world readiness (DD-022 3g) ---------------------------------------------------------------------------------------

    private static D.ClientWorld Client(ulong id, uint epoch, D.ClientWorldState state, bool wait = true, string step = "", string reason = "") =>
        new() { ClientId = id, WorldEpoch = epoch, State = state, Step = step, Reason = reason, WaitForMe = wait };

    private const D.ClientWorldState Loading = D.ClientWorldState.ClientWorldStateLoading, Ready = D.ClientWorldState.ClientWorldStateReady, Failed = D.ClientWorldState.ClientWorldStateFailed;

    /// <summary>The station as the transport drives it, without DDS: a world is published by the transport's pump taking a forced bump (or a new runtime's first epoch).</summary>
    private sealed class Station
    {
        public DpsHostService Service { get; } = new();

        public int TransportEpoch { get; private set; }

        public Station(int epoch = 1)
        {
            TransportEpoch = Service.NextEpoch();
            Assert.Equal(epoch, TransportEpoch);
            Service.MarkPublished((uint)TransportEpoch);
        }

        /// <summary>The transport's pump: adopt a forced epoch and publish it.</summary>
        public void Pump()
        {
            if (Service.TryTakeForced(TransportEpoch, out int epoch))
            {
                TransportEpoch = epoch;
                Service.MarkPublished((uint)epoch);
            }
        }
    }

    [Fact]
    public void Right_after_BeginWorld_the_world_is_not_ready_even_when_every_client_still_says_ready_for_the_old_one()
    {
        var station = new Station();
        var gate = (IClientWorldGate)station.Service;
        station.Service.Worlds.Update(Client(1, 1, Ready), writer: 1);
        station.Service.Worlds.Update(Client(2, 1, Ready), writer: 2);
        var load = Guid.NewGuid();

        gate.BeginWorld(load);
        // the race: the commit has happened, the station has not told the clients the new epoch
        Assert.False(gate.IsWorldReady(load, out _, out _));

        station.Pump();   // the next frame: the new epoch is published
        Assert.False(gate.IsWorldReady(load, out _, out _));   // both clients still say Ready for epoch 1

        station.Service.Worlds.Update(Client(1, 2, Loading, step: "terrain"), 1);
        Assert.False(gate.IsWorldReady(load, out _, out string? step));
        Assert.Equal("terrain", step);
        station.Service.Worlds.Update(Client(1, 2, Ready), 1);
        Assert.False(gate.IsWorldReady(load, out _, out _));   // client 2 has not answered
        station.Service.Worlds.Update(Client(2, 2, Ready), 2);
        Assert.True(gate.IsWorldReady(load, out string? failure, out step));
        Assert.Null(failure);
        Assert.Null(step);
    }

    [Fact]
    public void BeginWorld_bumps_the_epoch_without_any_boundary_and_the_transport_publishes_it_on_its_next_pump()
    {
        uint domain = Watch.NewDomain();
        using var sessions = new Watch.Watch<D.Session>(domain, "station-1", s => "session");
        using var rig = new Rig(domain);
        var service = (DpsHostService)rig.Service!;
        rig.Send(new HrotSession(ClusterPhase.Live, true, 1.0, 5.0));
        Assert.True(sessions.Until(w => w.Events.Count >= 1));
        Assert.Equal(1u, sessions.Events[^1].WorldEpoch);

        var load = Guid.NewGuid();
        service.BeginWorld(load);
        Assert.Equal(2, service.Epoch);
        Assert.False(service.IsWorldReady(load, out _, out _));   // not published yet: nothing is ready

        rig.Step();   // the next frame
        Assert.True(sessions.Until(w => w.Events[^1].WorldEpoch == 2u));
        Assert.Equal(2u, service.Published);
        Assert.True(service.IsWorldReady(load, out _, out _));   // no client waits

        service.BeginWorld(load);   // the same load again: nothing more
        Assert.Equal(2, service.Epoch);
    }

    [Fact]
    public void A_runtime_built_before_the_forced_epoch_was_taken_is_the_new_world()
    {
        var service = new DpsHostService();
        Assert.Equal(1, service.NextEpoch());
        service.MarkPublished(1);
        var load = Guid.NewGuid();
        service.BeginWorld(load);

        Assert.Equal(2, service.NextEpoch());   // the new runtime takes the forced epoch, no second bump
        Assert.Equal(3, service.NextEpoch());   // a boundary of its own is a bump of its own
        Assert.False(service.TryTakeForced(2, out _));
    }

    [Fact]
    public void An_unknown_load_is_ready_and_a_wait_for_me_false_client_is_never_waited_for()
    {
        var station = new Station();
        var gate = (IClientWorldGate)station.Service;
        Assert.True(gate.IsWorldReady(Guid.NewGuid(), out string? failure, out string? step));
        Assert.Null(failure);

        var load = Guid.NewGuid();
        gate.BeginWorld(load);
        station.Pump();
        Assert.True(gate.IsWorldReady(load, out _, out _));   // no client
        station.Service.Worlds.Update(Client(5, 1, Loading, wait: false), 5);
        Assert.True(gate.IsWorldReady(load, out _, out _));   // a passive viewer
        station.Service.Worlds.Update(Client(6, 2, Failed, reason: "no terrain"), 6);
        Assert.False(gate.IsWorldReady(load, out failure, out _));
        Assert.Equal("no terrain", failure);
        station.Service.Worlds.WriterLost(6);   // liveliness lost: no longer waited for
        Assert.True(gate.IsWorldReady(load, out _, out _));
    }

    [Fact]
    public void Seeded_random_client_updates_and_loads_agree_with_a_brute_force_evaluation_after_every_step()
    {
        for (int seed = 1; seed <= 40; seed++)
        {
            var rnd = new Random(seed);
            var station = new Station();
            var gate = (IClientWorldGate)station.Service;
            var model = new BruteForce();
            var loads = new List<Guid>();
            for (int op = 0; op < 120; op++)
            {
                switch (rnd.Next(6))
                {
                    case 0:
                    case 1:
                    case 2:
                    {
                        ulong id = (ulong)rnd.Next(1, 5);
                        uint epoch = (uint)Math.Max(1, model.Epoch - rnd.Next(0, 2));
                        var state = (D.ClientWorldState)rnd.Next(1, 4);
                        bool wait = rnd.Next(5) != 0;
                        string step = "s" + rnd.Next(3), reason = "r" + rnd.Next(3);
                        station.Service.Worlds.Update(Client(id, epoch, state, wait, step, reason), (long)id);
                        model.Update(id, epoch, state, wait, step, reason);
                        break;
                    }

                    case 3:
                    {
                        ulong id = (ulong)rnd.Next(1, 5);
                        station.Service.Worlds.WriterLost((long)id);
                        model.Lost(id);
                        break;
                    }

                    case 4:
                    {
                        var load = Guid.NewGuid();
                        loads.Add(load);
                        gate.BeginWorld(load);
                        model.Begin(load);
                        break;
                    }

                    default:
                        station.Pump();
                        model.Pump();
                        break;
                }

                foreach (var load in loads.Append(Guid.NewGuid()))
                {
                    bool got = gate.IsWorldReady(load, out string? failure, out string? step);
                    var (ready, expectedFailure, expectedStep) = model.Ready(load);
                    Assert.True(ready == got, $"seed {seed} op {op}: ready {got}, expected {ready}");
                    Assert.Equal(expectedFailure, failure);
                    Assert.Equal(expectedStep, step);
                }
            }
        }
    }

    /// <summary>The definition, written out plainly and independent of the implementation.</summary>
    private sealed class BruteForce
    {
        private readonly List<(ulong Id, uint Epoch, D.ClientWorldState State, bool Wait, string Step, string Reason)> _clients = new();
        private readonly Dictionary<Guid, uint> _loads = new();
        private uint _published = 1;

        public uint Epoch { get; private set; } = 1;

        private bool _forced;

        public void Update(ulong id, uint epoch, D.ClientWorldState state, bool wait, string step, string reason)
        {
            int i = _clients.FindIndex(c => c.Id == id);
            if (i < 0)
            {
                _clients.Add((id, epoch, state, wait, step, reason));
            }
            else
            {
                _clients[i] = (id, epoch, state, wait, step, reason);
            }
        }

        public void Lost(ulong id) => _clients.RemoveAll(c => c.Id == id);

        public void Begin(Guid load)
        {
            if (_loads.ContainsKey(load))
            {
                return;
            }

            Epoch++;
            _forced = true;
            _loads[load] = Epoch;
        }

        public void Pump()
        {
            if (_forced)
            {
                _forced = false;
                _published = Epoch;
            }
        }

        public (bool Ready, string? Failure, string? Step) Ready(Guid load)
        {
            if (!_loads.TryGetValue(load, out uint epoch))
            {
                return (true, null, null);
            }

            if (_published < epoch)
            {
                return (false, null, null);
            }

            var waited = _clients.Where(c => c.Wait).ToList();
            bool ready = waited.All(c => c.Epoch == epoch && c.State == D.ClientWorldState.ClientWorldStateReady);
            string? failure = waited.Where(c => c.Epoch == epoch && c.State == D.ClientWorldState.ClientWorldStateFailed).Select(c => c.Reason).FirstOrDefault();
            string? step = waited.Where(c => c.State == D.ClientWorldState.ClientWorldStateLoading).Select(c => c.Step).FirstOrDefault();
            return (ready, failure, step);
        }
    }

    [Fact]
    public async Task A_client_world_over_the_wire_is_tracked_and_dropped_when_its_writer_goes_away()
    {
        uint domain = Watch.NewDomain();
        using var rig = new Rig(domain);
        var service = (DpsHostService)rig.Service!;
        var writerSide = new DdsParticipant(domain, defaultPartition: "station-1");
        var writer = new DdsWriter<D.ClientWorld>(writerSide);
        Assert.True(await writer.WaitForReaderAsync(TimeSpan.FromSeconds(5)));

        writer.Write(Client(77, 1, Loading, step: "terrain city"));
        Assert.True(DpsOutboundTests.WaitFor(() =>
        {
            rig.Step();
            return service.Worlds.Clients.Count == 1;
        }));
        Assert.Equal((77UL, Loading, "terrain city"), (service.Worlds.Clients[0].ClientId, service.Worlds.Clients[0].State, service.Worlds.Clients[0].Step));

        writer.Write(Client(77, 1, Ready));
        Assert.True(DpsOutboundTests.WaitFor(() =>
        {
            rig.Step();
            return service.Worlds.Clients[0].State == Ready;
        }));
        Assert.True(service.Worlds.Evaluate(1, out _, out _));

        writer.Dispose();
        writerSide.Dispose();
        Assert.True(DpsOutboundTests.WaitFor(() =>
        {
            rig.Step();
            return service.Worlds.Clients.Count == 0;
        }, 10000));
    }
}
