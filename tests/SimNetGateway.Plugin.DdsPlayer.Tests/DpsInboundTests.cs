using CycloneDDS.Runtime;
using SimNetGateway.Gateway.HrotDis;
using SimNetGateway.Protocol.Hrot;
using D = SimNetGateway.Protocol.DdsPlayer;
using Rig = SimNetGateway.Plugin.DdsPlayer.Tests.DpsOutboundTests.Rig;
using Watch = SimNetGateway.Plugin.DdsPlayer.Tests.DpsOutboundTests;

namespace SimNetGateway.Plugin.DdsPlayer.Tests;

/// <summary>The player topics driving the player-station seam: join, control frames, leave, lost client, one player at a time, the slot and the unit published on change.</summary>
public class DpsInboundTests
{
    private sealed class RecordingStation : IPlayerStation
    {
        public List<PlayerJoin> Joins { get; } = new();

        public List<PlayerControl> Controls { get; } = new();

        public int Leaves { get; private set; }

        public bool JoinResult { get; set; } = true;

        public PlayerSlotSnapshot Slot { get; set; } = new(PlayerSlotState.Choosing, 0, 0.0, 1);

        public List<PlayerOffer> OfferList { get; } = new();

        public IReadOnlyList<PlayerOffer> Offers => OfferList;

        public PlayerUnitStatus Unit { get; set; } = PlayerUnitStatus.None;

        public bool Join(PlayerJoin request)
        {
            Joins.Add(request);
            return JoinResult;
        }

        public void Control(in PlayerControl frame) => Controls.Add(frame);

        public void Leave() => Leaves++;
    }

    /// <summary>A client: a participant in the station's partition with the three player writers.</summary>
    private sealed class Client : IDisposable
    {
        private readonly DdsParticipant _participant;
        private readonly DdsWriter<D.JoinRequest> _join;
        private readonly DdsWriter<D.ControlFrame> _control;
        private readonly DdsWriter<D.Leave> _leave;

        public Client(uint domain, string partition = "station-1")
        {
            _participant = new DdsParticipant(domain, defaultPartition: partition);
            _join = new DdsWriter<D.JoinRequest>(_participant);
            _control = new DdsWriter<D.ControlFrame>(_participant);
            _leave = new DdsWriter<D.Leave>(_participant);
        }

        public void Join(uint seq, int unit, D.JoinKind kind = D.JoinKind.JoinKindUnit, string role = "") =>
            _join.Write(new D.JoinRequest { RequestSeq = seq, Kind = kind, UnitId = unit, Role = role });

        public void Control(D.ControlFrame frame) => _control.Write(frame);

        public void Leave(uint seq) => _leave.Write(new D.Leave { Seq = seq });

        public void Dispose()
        {
            _join.Dispose();
            _control.Dispose();
            _leave.Dispose();
            _participant.Dispose();
        }
    }

    /// <summary>Pumps the plugin (each step is one host frame) until the condition holds.</summary>
    private static bool Until(Rig rig, Func<bool> condition, int timeoutMs = 6000) => Watch.WaitFor(() =>
    {
        rig.Step();
        return condition();
    }, timeoutMs);

    private static void Frames(Rig rig, int n = 15)
    {
        for (int i = 0; i < n; i++)
        {
            rig.Step();
            Thread.Sleep(10);
        }
    }

    [Fact]
    public void Join_request_calls_join_once()
    {
        uint domain = Watch.NewDomain();
        var station = new RecordingStation();
        using var rig = new Rig(domain, station: station);
        using var client = new Client(domain);

        client.Join(1, 1201);

        Assert.True(Until(rig, () => station.Joins.Count == 1));
        Frames(rig);
        Assert.Equal([new PlayerJoin(PlayerJoinKind.Unit, 1201, null)], station.Joins);

        client.Join(2, 0, D.JoinKind.JoinKindRole, "Rifleman");
        Assert.True(Until(rig, () => station.Joins.Count == 2));
        Assert.Equal(new PlayerJoin(PlayerJoinKind.Role, 0, "Rifleman"), station.Joins[1]);

        client.Join(3, 0, D.JoinKind.JoinKindObserve);
        Assert.True(Until(rig, () => station.Joins.Count == 3));
        Assert.Equal(PlayerJoinKind.Observe, station.Joins[2].Kind);
    }

    [Fact]
    public void Control_frame_is_passed_through_newest_seq_wins()
    {
        uint domain = Watch.NewDomain();
        var station = new RecordingStation();
        using var rig = new Rig(domain, station: station);
        using var client = new Client(domain);
        client.Join(1, 1201);
        Assert.True(Until(rig, () => station.Joins.Count == 1));

        client.Control(new D.ControlFrame
        {
            Seq = 5, Forward = 1f, Lateral = -0.5f, Steer = 0.25f, Yaw = 0.75f, Vertical = 0.125f, Gait = 2, Trigger = 1, Mount = 1, AimPoint = new D.Vec3d { X = 10, Y = 20, Z = 30 },
            Stance = 3, ShotSeq = 4, CancelSeq = 5, AmmoSeq = 6, ShotTarget = 2002, UseSeq = 7, UseVerb = 1, UseTarget = 2003, Lean = 2, Binoculars = 1,
            LookFrom = new D.Vec3d { X = 1, Y = 2, Z = 3 }, LookDir = new D.Vec3f { Y = 1 }, FirePref = 2,
        });
        Assert.True(Until(rig, () => station.Controls.Count == 1));
        var c = station.Controls[0];
        Assert.Equal((5u, 1f, -0.5f, 0.25f, 0.75f, 0.125f, (byte)2, true, (byte)1), (c.Seq, c.Forward, c.Lateral, c.Steer, c.Yaw, c.Vertical, c.Gait, c.Trigger, c.Mount));
        Assert.Equal(new HrotVec3(10, 20, 30), c.AimPoint);
        Assert.Equal(((byte)3, (byte)4, (byte)5, (byte)6, 2002, (byte)7, (byte)1, 2003), (c.Stance, c.ShotSeq, c.CancelSeq, c.AmmoSeq, c.ShotTarget, c.UseSeq, c.UseVerb, c.UseTarget));
        Assert.Equal((2f, true, (byte)2), (c.Lean, c.Binoculars, c.FirePref));
        Assert.Equal(new HrotVec3(1, 2, 3), c.LookFrom);
        Assert.Equal(new HrotVec3(0, 1, 0), c.LookDir);

        client.Control(new D.ControlFrame { Seq = 7, Forward = 0.5f });
        Assert.True(Until(rig, () => station.Controls.Count == 2));
        client.Control(new D.ControlFrame { Seq = 6, Forward = -1f });   // late: older than the one applied
        Frames(rig);

        Assert.Equal([5u, 7u], station.Controls.Select(f => f.Seq).ToArray());
    }

    [Fact]
    public void Leave_calls_leave()
    {
        uint domain = Watch.NewDomain();
        var station = new RecordingStation();
        using var rig = new Rig(domain, station: station);
        using var client = new Client(domain);

        client.Leave(1);

        Assert.True(Until(rig, () => station.Leaves == 1));
        Frames(rig);
        Assert.Equal(1, station.Leaves);
    }

    [Fact]
    public void Lost_client_releases_once_and_can_rejoin()
    {
        uint domain = Watch.NewDomain();
        var station = new RecordingStation();
        using var rig = new Rig(domain, station: station);
        var first = new Client(domain);
        first.Join(1, 1201);
        first.Control(new D.ControlFrame { Seq = 1 });
        Assert.True(Until(rig, () => station.Joins.Count == 1 && station.Controls.Count == 1));

        first.Dispose();   // the client vanishes: no Leave, only the liveliness is lost

        Assert.True(Until(rig, () => station.Leaves == 1), "the lost client's unit is released");
        Frames(rig, 30);
        Assert.Equal(1, station.Leaves);   // exactly once

        using var second = new Client(domain);
        second.Join(1, 1202);
        Assert.True(Until(rig, () => station.Joins.Count == 2), "the next client can join");
        Assert.Equal(1202, station.Joins[1].UnitId);
    }

    [Fact]
    public void Second_player_writer_is_ignored_with_one_warning()
    {
        uint domain = Watch.NewDomain();
        var station = new RecordingStation();
        var log = new List<string>();
        using var rig = new Rig(domain, station: station, log: log.Add);
        using var first = new Client(domain);
        first.Join(1, 1201);
        Assert.True(Until(rig, () => station.Joins.Count == 1));

        using var second = new Client(domain);
        second.Join(1, 1202);
        Frames(rig, 20);
        second.Join(2, 1203);
        Frames(rig, 20);

        Assert.Equal([1201], station.Joins.Select(j => j.UnitId).ToArray());
        Assert.Equal(1, log.Count(l => l.Contains("second player writer")));

        first.Join(2, 1204);   // the first keeps playing
        Assert.True(Until(rig, () => station.Joins.Count == 2));
        Assert.Equal(1204, station.Joins[1].UnitId);
    }

    [Fact]
    public void Join_in_replay_is_refused_as_observing()
    {
        uint domain = Watch.NewDomain();
        var station = new RecordingStation { JoinResult = false, Slot = new PlayerSlotSnapshot(PlayerSlotState.Observing, 0, 0.0, 1) };
        using var slots = new Watch.Watch<D.PlayerSlot>(domain, "station-1", s => "slot");
        using var rig = new Rig(domain, station: station);
        using var client = new Client(domain);

        client.Join(1, 1201);

        Assert.True(Until(rig, () => station.Joins.Count == 1));
        Assert.True(slots.Until(w =>
        {
            rig.Step();
            return w.Alive.TryGetValue("slot", out var s) && s.State == D.SlotState.SlotStateObserving;
        }));
    }

    [Fact]
    public void Player_slot_and_offers_are_published_on_change()
    {
        uint domain = Watch.NewDomain();
        var station = new RecordingStation();
        station.OfferList.Add(new PlayerOffer(1201, "Rifleman", 1));
        station.OfferList.Add(new PlayerOffer(1202, null, 2));
        using var slots = new Watch.Watch<D.PlayerSlot>(domain, "station-1", s => "slot");
        using var rig = new Rig(domain, station: station);

        Frames(rig);
        Assert.Equal(1, rig.Transport.PlayerSlot.Written);   // once, not every frame
        Assert.True(slots.Until(w => w.Alive.ContainsKey("slot")));
        var slot = slots.Alive["slot"];
        Assert.Equal(D.SlotState.SlotStateChoosing, slot.State);
        Assert.Equal([(1201, "Rifleman", (byte)1), (1202, "", (byte)2)], slot.Offers.Select(o => (o.Id, o.Name, o.Side)).ToArray());

        station.Slot = new PlayerSlotSnapshot(PlayerSlotState.Playing, 1201, 0.0, 2);
        Frames(rig);
        Assert.Equal(2, rig.Transport.PlayerSlot.Written);
        Assert.True(slots.Until(w => w.Alive["slot"].State == D.SlotState.SlotStatePlaying));
        Assert.Equal(1201, slots.Alive["slot"].UnitId);
        Assert.Equal((byte)1, slots.Alive["slot"].Side);

        station.OfferList.RemoveAt(1);   // the offers changed, the slot did not
        Frames(rig);
        Assert.Equal(3, rig.Transport.PlayerSlot.Written);
        Assert.True(slots.Until(w => w.Alive["slot"].Offers.Count == 1));
    }

    [Fact]
    public void Own_unit_carries_ammo_health_and_use_verdict()
    {
        uint domain = Watch.NewDomain();
        var station = new RecordingStation { Slot = new PlayerSlotSnapshot(PlayerSlotState.Playing, 1201, 0.0, 1) };
        station.Unit = new PlayerUnitStatus(80.0, 100.0, [((byte)0, 77L, 30), ((byte)1, 0L, 5)], 3, 55);
        using var units = new Watch.Watch<D.OwnUnit>(domain, "station-1", u => "unit");
        using var rig = new Rig(domain, station: station);

        Frames(rig);

        Assert.True(units.Until(w => w.Alive.ContainsKey("unit")));
        var u = units.Alive["unit"];
        Assert.Equal((1201, 80f, 100f, true, (byte)3, 55), (u.UnitId, u.Health, u.MaxHealth, u.CanUse, u.UseVerb, u.UseTargetId));
        Assert.Equal([((byte)0, 30), ((byte)1, 5)], u.Ammo.Select(a => (a.Slot, a.Rounds)).ToArray());
        Assert.Equal("77", u.Ammo[0].AmmoType);
        Assert.Equal(1, rig.Transport.OwnUnit.Written);

        station.Unit = new PlayerUnitStatus(70.0, 100.0, [((byte)0, 77L, 29)], 0, 0);
        Frames(rig);
        Assert.Equal(2, rig.Transport.OwnUnit.Written);
        Assert.True(units.Until(w => w.Alive["unit"].Health == 70f));
        Assert.False(units.Alive["unit"].CanUse);
    }

    [Fact]
    public void No_player_station_means_view_only()
    {
        uint domain = Watch.NewDomain();
        using var slots = new Watch.Watch<D.PlayerSlot>(domain, "station-1", s => "slot");
        using var rig = new Rig(domain, station: null);
        using var client = new Client(domain);

        client.Join(1, 1201);
        client.Control(new D.ControlFrame { Seq = 1 });
        client.Leave(1);
        Frames(rig, 20);

        Assert.True(slots.Until(w => w.Alive.TryGetValue("slot", out var s) && s.State == D.SlotState.SlotStateObserving));
        Assert.Empty(slots.Alive["slot"].Offers);
        Assert.Equal(1, rig.Transport.PlayerSlot.Written);
    }
}
