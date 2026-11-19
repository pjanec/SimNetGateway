using System.Numerics;
using Fdp.Core;
using Fdp.Toolkit.Behavior.Possession;
using Fdp.Toolkit.Combat.Components;
using Fdp.Toolkit.Orchestration;
using Hrot.Common;
using SimNetGateway.Gateway.HrotDis;
using SimNetGateway.Protocol.Hrot;

namespace SimNetGateway.HrotHost.Tests;

/// <summary>The player-station seam over HROT's player mode (DD-017 3.6, editor step): offers, join, control frames, leave, slot and unit status.</summary>
public class PlayerStationTests
{
    private const int Unit = 2001;

    private sealed class Stage : PackHarness
    {
        private long _ms = 1_000;

        public Stage()
            : base(dis: true)
        {
            RegisterTemplate(300, 3);
            Station.NowMs = () => _ms;
            Steps(3);
        }

        public HrotPlayerStation Station => System.PlayerStation;

        public void AdvanceMs(long ms) => _ms += ms;

        public Entity AddPlayable(int id = Unit, ForceId side = ForceId.Friend, string slot = "Rifleman")
        {
            var e = AddHrotEntity(id, new Vector3(1, 2, 0), tkb: 300);
            World.AddComponent(e, new Hrot.Common.Playable { Side = side, SlotName = new FixedString32(slot) });
            return e;
        }

        /// <summary>What HROT's possession system does when it applies the request: the unit is held by this node.</summary>
        public void Hold(Entity e, int station = LocalNodeId)
        {
            if (!World.IsComponentTypeRegistered<PossessedBy>())
            {
                World.RegisterComponent<PossessedBy>();
            }

            var held = new PossessedBy { StationNodeId = (NodeId)station, Mode = PossessionMode.Fps, Epoch = 1 };
            if (World.HasComponent<PossessedBy>(e))
            {
                World.SetComponent(e, held);
            }
            else
            {
                World.AddComponent(e, held);
            }
        }

        public void Release(Entity e)
        {
            if (World.HasComponent<PossessedBy>(e))
            {
                World.RemoveComponent<PossessedBy>(e);
            }
        }

        public List<T> Events<T>()
            where T : unmanaged
        {
            World.Bus.SwapBuffers();
            var list = new List<T>();
            if (World.Bus.IsRegistered<T>())
            {
                foreach (ref readonly var e in World.ReadEvents<T>())
                {
                    list.Add(e);
                }
            }

            return list;
        }

        /// <summary>Joins the unit and lets HROT grant it: the slot is Playing.</summary>
        public Entity Play()
        {
            var e = AddPlayable();
            Steps(2);
            Assert.True(Station.Join(new PlayerJoin(PlayerJoinKind.Unit, Unit, null)));
            Hold(e);
            Steps(2);
            Assert.Equal(PlayerSlotState.Playing, Station.Slot.State);
            Events<PossessRequestEvent>();   // drain the request
            return e;
        }
    }

    private static PlayerControl Frame() => new(
        Seq: 77, Forward: 1f, Lateral: -0.5f, Steer: 0.25f, Yaw: 0.75f, Vertical: 0.125f, Gait: 2, Trigger: true, Mount: 1, AimPoint: new HrotVec3(10, 20, 30), Stance: 3,
        ShotSeq: 4, CancelSeq: 5, AmmoSeq: 6, ShotTarget: 2002, UseSeq: 7, UseVerb: 1, UseTarget: 2003, Lean: 2f, Binoculars: true, LookFrom: new HrotVec3(1, 2, 3),
        LookDir: new HrotVec3(0, 1, 0), FirePref: 2);

    [Fact]
    public void Offers_are_the_placed_playable_units()
    {
        using var h = new Stage();
        h.AddPlayable(2001, ForceId.Friend, "Rifleman");
        h.AddPlayable(2002, ForceId.Hostile, "Sniper");
        h.AddHrotEntity(2003, Vector3.Zero, tkb: 300);   // not playable
        h.Steps(2);

        Assert.Equal([new PlayerOffer(2001, "Rifleman", 1), new PlayerOffer(2002, "Sniper", 2)], h.Station.Offers);
    }

    [Fact]
    public void Join_unit_possesses_it()
    {
        using var h = new Stage();
        var e = h.AddPlayable();
        h.Steps(2);
        Assert.Equal(PlayerSlotState.Choosing, h.Station.Slot.State);

        Assert.True(h.Station.Join(new PlayerJoin(PlayerJoinKind.Unit, Unit, null)));

        var requests = h.Events<PossessRequestEvent>();
        var request = Assert.Single(requests);
        Assert.Equal(PackHarness.LocalNodeId, (int)request.StationNodeId);
        Assert.Equal(e, request.Unit);
        Assert.Equal(PossessionMode.Fps, request.Mode);
        Assert.Equal(PlayerSlotState.TakingControl, h.Station.Slot.State);
        Assert.Equal(Unit, h.Station.Slot.UnitId);

        h.Hold(e);
        h.Steps(1);
        Assert.Equal(PlayerSlotState.Playing, h.Station.Slot.State);
    }

    [Fact]
    public void Join_of_a_unit_that_is_not_offered_is_refused()
    {
        using var h = new Stage();
        h.AddHrotEntity(2003, Vector3.Zero, tkb: 300);
        h.Steps(2);

        Assert.False(h.Station.Join(new PlayerJoin(PlayerJoinKind.Unit, 2003, null)));

        Assert.Empty(h.Events<PossessRequestEvent>());
        Assert.Equal(PlayerSlotState.Choosing, h.Station.Slot.State);
    }

    [Fact]
    public void Join_role_is_refused_and_observe_observes()
    {
        using var h = new Stage();
        h.AddPlayable();
        h.Steps(2);

        Assert.False(h.Station.Join(new PlayerJoin(PlayerJoinKind.Role, 0, "Rifleman")));
        Assert.Equal(PlayerSlotState.Choosing, h.Station.Slot.State);

        Assert.True(h.Station.Join(new PlayerJoin(PlayerJoinKind.Observe, 0, null)));
        Assert.Equal(PlayerSlotState.Observing, h.Station.Slot.State);
        Assert.Empty(h.Events<PossessRequestEvent>());
    }

    [Fact]
    public void Control_frame_maps_every_field_to_the_hrot_event()
    {
        using var h = new Stage();
        var e = h.Play();

        h.Station.Control(Frame());

        var f = Assert.Single(h.Events<OperatorControlFrameEvent>());
        Assert.Equal(e, f.Unit);
        Assert.Equal(PackHarness.LocalNodeId, (int)f.StationNodeId);
        Assert.False(f.IsRemote);
        Assert.Equal(77u, f.Seq);
        Assert.Equal(1f, f.Forward);
        Assert.Equal(-0.5f, f.Lateral);
        Assert.Equal(0.25f, f.Steer);
        Assert.Equal(0.75f, f.Yaw);
        Assert.Equal(0.125f, f.Vertical);
        Assert.Equal((byte)2, f.Gait);
        Assert.Equal((byte)1, f.Trigger);
        Assert.Equal((byte)1, f.Mount);
        Assert.Equal(new Vector3(10, 20, 30), f.AimPoint);
        Assert.Equal((byte)3, f.Stance);
        Assert.Equal((byte)4, f.ShotSeq);
        Assert.Equal((byte)5, f.CancelSeq);
        Assert.Equal((byte)6, f.AmmoSeq);
        Assert.Equal(2002, f.ShotTarget);
        Assert.Equal((byte)7, f.UseSeq);
        Assert.Equal((byte)1, f.UseVerb);
        Assert.Equal(2003, f.UseTarget);
        Assert.Equal((byte)2, f.Lean);
        Assert.Equal((byte)1, f.Binoculars);
        Assert.Equal(new Vector3(1, 2, 3), f.LookFrom);
        Assert.Equal(new Vector3(0, 1, 0), f.LookDir);
        Assert.Equal((byte)2, f.FirePref);
    }

    [Fact]
    public void Control_without_possession_is_ignored()
    {
        using var h = new Stage();
        h.AddPlayable();
        h.Steps(2);

        h.Station.Control(Frame());

        Assert.Empty(h.Events<OperatorControlFrameEvent>());
    }

    [Fact]
    public void Leave_releases_once()
    {
        using var h = new Stage();
        var e = h.Play();

        h.Station.Leave();
        var release = Assert.Single(h.Events<ReleasePossessionEvent>());
        Assert.Equal(e, release.Unit);
        Assert.Equal(PackHarness.LocalNodeId, (int)release.StationNodeId);
        Assert.Equal(PlayerSlotState.Choosing, h.Station.Slot.State);
        Assert.Equal(0, h.Station.Slot.UnitId);

        h.Station.Leave();
        Assert.Empty(h.Events<ReleasePossessionEvent>());
    }

    [Fact]
    public void Join_in_replay_returns_false_and_slot_is_observing()
    {
        using var h = new Stage();
        h.AddPlayable();
        h.State = Fdp.Toolkit.Orchestration.ClusterState.OperatingReplay;
        h.Steps(2);

        Assert.False(h.Station.Join(new PlayerJoin(PlayerJoinKind.Unit, Unit, null)));

        Assert.Equal(PlayerSlotState.Observing, h.Station.Slot.State);
        Assert.Empty(h.Events<PossessRequestEvent>());
        Assert.Empty(h.Station.Offers);

        // the recording ends: back to choosing
        h.State = Fdp.Toolkit.Orchestration.ClusterState.OperatingLive;
        h.Steps(2);
        Assert.Equal(PlayerSlotState.Choosing, h.Station.Slot.State);
    }

    [Fact]
    public void Slot_follows_hrot_player_states()
    {
        using var h = new Stage();
        var e = h.AddPlayable();
        h.Steps(2);
        var slot = h.Station.Slot;
        Assert.Equal(PlayerSlotState.Choosing, slot.State);

        // waiting while no scenario runs
        h.State = Fdp.Toolkit.Orchestration.ClusterState.Idle;
        h.Steps(2);
        Assert.Equal(PlayerSlotState.Waiting, h.Station.Slot.State);
        Assert.True(h.Station.Slot.Version > slot.Version);
        h.State = Fdp.Toolkit.Orchestration.ClusterState.OperatingLive;
        h.Steps(2);
        Assert.Equal(PlayerSlotState.Choosing, h.Station.Slot.State);

        // choosing -> taking control -> playing
        var before = h.Station.Slot.Version;
        Assert.True(h.Station.Join(new PlayerJoin(PlayerJoinKind.Unit, Unit, null)));
        Assert.Equal(before + 1, h.Station.Slot.Version);
        h.Hold(e);
        h.Steps(1);
        Assert.Equal(PlayerSlotState.Playing, h.Station.Slot.State);
        Assert.Equal(before + 2, h.Station.Slot.Version);

        // steady frames change nothing
        h.Steps(3);
        Assert.Equal(before + 2, h.Station.Slot.Version);

        // HROT releases the unit (another reason than our own leave): choosing again
        h.Release(e);
        h.Steps(1);
        Assert.Equal(PlayerSlotState.Choosing, h.Station.Slot.State);
        Assert.Equal(0, h.Station.Slot.UnitId);
    }

    [Fact]
    public void A_killed_unit_is_dead_with_no_respawn()
    {
        using var h = new Stage();
        var e = h.Play();

        h.World.SetComponent(e, new Health { Current = 0f, Max = 100f });
        h.Release(e);   // HROT's possession system lets a killed unit go on its own
        h.Steps(1);

        Assert.Equal(PlayerSlotState.Dead, h.Station.Slot.State);
        Assert.Equal(Unit, h.Station.Slot.UnitId);
        Assert.Equal(0.0, h.Station.Slot.RespawnAt);

        h.Station.Leave();
        Assert.Equal(PlayerSlotState.Choosing, h.Station.Slot.State);
        Assert.Empty(h.Events<ReleasePossessionEvent>());   // nothing held: no call
    }

    [Fact]
    public void An_ungranted_possession_times_out_back_to_choosing()
    {
        using var h = new Stage();
        h.AddPlayable();
        h.Steps(2);
        Assert.True(h.Station.Join(new PlayerJoin(PlayerJoinKind.Unit, Unit, null)));
        h.Steps(1);
        Assert.Equal(PlayerSlotState.TakingControl, h.Station.Slot.State);

        h.AdvanceMs(10_001);
        h.Steps(1);

        Assert.Equal(PlayerSlotState.Choosing, h.Station.Slot.State);
        Assert.Equal(0, h.Station.Slot.UnitId);
    }

    [Fact]
    public void A_world_boundary_returns_the_slot_to_waiting()
    {
        using var h = new Stage();
        h.Play();

        h.Station.Boundary();

        Assert.Equal(PlayerSlotState.Waiting, h.Station.Slot.State);
        Assert.Equal(0, h.Station.Slot.UnitId);
        Assert.Equal(PlayerUnitStatus.None, h.Station.Unit);
    }

    [Fact]
    public void Unit_status_reads_health_and_ammunition()
    {
        using var h = new Stage();
        var e = h.Play();
        h.World.SetComponent(e, new Health { Current = 80f, Max = 100f });
        if (!h.World.IsComponentTypeRegistered<WeaponAmmo>())
        {
            h.World.RegisterComponent<WeaponAmmo>();
        }

        h.World.AddComponent(e, new WeaponAmmo { Count = 2, Ammo0 = 30, Load0 = 77, Ammo1 = 5, Load1 = 0 });
        h.Steps(1);

        var unit = h.Station.Unit;
        Assert.Equal(80.0, unit.Health);
        Assert.Equal(100.0, unit.HealthMax);
        Assert.Equal([((byte)0, 77L, 30), ((byte)1, 0L, 5)], unit.Ammo);
        Assert.Equal((byte)0, unit.UseVerb);
        Assert.Equal(0, unit.UseTarget);
    }

    [Fact]
    public void The_player_station_is_handed_to_the_plugins()
    {
        using var h = new Stage();
        Assert.NotNull(h.System.Runtime);
        Assert.IsAssignableFrom<IPlayerStation>(h.Station);
    }
}
