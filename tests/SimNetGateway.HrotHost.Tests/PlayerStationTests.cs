using System.Numerics;
using Fdp.Core;
using CarKinem.Core;
using CarKinem.Spatial;
using CarKinem.Systems;
using CarKinem.Trajectory;
using Fdp.Toolkit.Behavior.Possession;
using Fdp.Toolkit.Combat.Components;
using Fdp.Toolkit.Replication.Components;
using Fdp.Toolkit.Perception.Radar;
using DISnet;
using Fdp.Toolkit.Orchestration;
using Fdp.Toolkit.Replication.Services;
using Hrot.Common;
using CycloneDDS.Runtime;
using SimNetGateway.Gateway.HrotDis;
using SimNetGateway.Plugin.DdsPlayer;
using D = SimNetGateway.Protocol.DdsPlayer;
using SimNetGateway.Protocol.Hrot;
using GatewayConfiguration = SimNetGateway.Gateway.HrotDis.GatewayConfiguration;

namespace SimNetGateway.HrotHost.Tests;

/// <summary>The player-station seam over HROT's player mode (DD-017 3.6, editor step): offers, join, control frames, leave, slot and unit status.</summary>
public class PlayerStationTests
{
    private const int Unit = 2001;

    /// <summary>The gateway player is seat 1: station node | 1 << 16 (DD-023 D-11), no longer the node's own station.</summary>
    private const int SeatStation = PackHarness.LocalNodeId | (1 << 16);

    private sealed class Stage : PackHarness
    {
        private long _ms = 1_000;

        public Stage(ProbePlugin? probe = null)
            : base(dis: true, plugins: probe is null ? null : [probe], configure: probe is null ? null : c => c.WithSide(ProbeSide.SideKey, new ProbeSettings(false)))
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
        public void Hold(Entity e, int station = SeatStation)
        {
            if (!World.IsComponentTypeRegistered<PossessedBy>())
            {
                World.RegisterComponent<PossessedBy>();
            }

            var held = new PossessedBy { StationNodeId = (StationId)station, Mode = PossessionMode.Fps, Epoch = 1 };
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
            return Read<T>();
        }

        /// <summary>The events readable now (published in the last step), without swapping the bus.</summary>
        public List<T> Read<T>()
            where T : unmanaged
        {
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
        Assert.Equal(SeatStation, (int)request.StationNodeId);
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
        Assert.Equal(SeatStation, (int)f.StationNodeId);
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

    /// <summary>
    /// T-040 end to end: a DPS control frame's Lateral reaches HROT's <c>OperatorControlFrameEvent</c> bit for bit and, through HROT's own person integrator
    /// (<c>CarKinematicsSystem</c> / <c>ManualMotion</c>), moves the possessed man to his LEFT for + and to his RIGHT for - (ENU: x east, y north, yaw counter-clockwise;
    /// body-left = heading + 90 degrees). Station -> event is the gateway's code; event -> <c>ManualDrive</c> is HROT's <c>PossessionSystem</c> (event to <c>OperatorInput</c>) and
    /// <c>OperatorControlBehavior.ManualControl</c> (<c>OperatorInput</c> to <c>ManualDrive</c>), two field-for-field copies that live in the behaviour layer (Hrot.AI.Behaviors,
    /// not referenced by these tests) and are done by hand here; the integrator itself is HROT's real system.
    /// </summary>
    [Theory]
    [InlineData(1f, 0.7f)]
    [InlineData(-1f, 0.7f)]
    [InlineData(1f, -2.2f)]
    [InlineData(-1f, -2.2f)]
    [InlineData(0.5f, 0f)]
    public void Control_frame_lateral_plus_moves_the_man_to_his_left_minus_to_his_right(float lateral, float heading)
    {
        using var h = new Stage();
        var e = h.Play();
        h.World.RegisterComponent<VehicleState>();
        h.World.RegisterComponent<VehicleParams>();
        h.World.RegisterComponent<NavState>();
        h.World.RegisterComponent<SpatialGridData>();
        h.World.RegisterComponent<Fdp.Toolkit.Navigation.ManualDrive>();
        h.World.SetComponent(e, new SimTransform { Position = Vector3.Zero, Rotation = SimMath.FromYaw(heading) });
        h.World.SetAuthority<SimTransform>(e, true);
        h.World.AddComponent(e, new VehicleState());
        h.World.AddComponent(e, VehiclePresets.GetPreset(VehicleClass.Pedestrian));
        h.World.AddComponent(e, new NavState { Mode = KinematicsMode.Manual });
        h.World.AddComponent(e, new Fdp.Toolkit.Navigation.ManualDrive());
        var spatial = new SpatialHashSystem();
        var kin = new CarKinematicsSystem(new TrajectoryPoolManager());
        h.Events<OperatorControlFrameEvent>();   // drain

        for (uint seq = 1; seq <= 120; seq++)
        {
            h.Station.Control(Frame() with { Seq = seq, Forward = 0f, Lateral = lateral, Steer = 0f, Yaw = heading, Gait = 0 });
            var ev = Assert.Single(h.Events<OperatorControlFrameEvent>());
            Assert.Equal(BitConverter.SingleToInt32Bits(lateral), BitConverter.SingleToInt32Bits(ev.Lateral));   // the sign and the value reach HROT untouched
            Assert.Equal(heading, ev.Yaw);
            h.World.SetComponent(e, new Fdp.Toolkit.Navigation.ManualDrive { Forward = ev.Forward, Lateral = ev.Lateral, Steer = ev.Steer, Yaw = ev.Yaw, Vertical = ev.Vertical, Gait = ev.Gait, Seq = ev.Seq });
            spatial.Execute(h.World, 1f / 60f);
            kin.Execute(h.World, 1f / 60f);
        }

        var pos = h.World.GetComponent<SimTransform>(e).Position;
        var fwd = Vector3.Transform(Vector3.UnitX, h.World.GetComponent<SimTransform>(e).Rotation);
        float yaw = MathF.Atan2(fwd.Y, fwd.X);
        var left = new Vector2(-MathF.Sin(heading), MathF.Cos(heading));   // heading + 90 degrees
        float toLeft = pos.X * left.X + pos.Y * left.Y;
        float along = pos.X * MathF.Cos(heading) + pos.Y * MathF.Sin(heading);
        Assert.True(MathF.Sign(toLeft) == MathF.Sign(lateral) && MathF.Abs(toLeft) > 0.5f * MathF.Abs(lateral), $"displaced {toLeft:F2} m to his left for lateral {lateral} (at {pos})");
        Assert.InRange(MathF.Abs(along), 0f, 0.05f);                                                      // not forward or back
        Assert.InRange(MathF.Abs(MathF.IEEERemainder(yaw - heading, 2f * MathF.PI)), 0f, 0.01f);         // the heading did not change
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
        Assert.Equal(SeatStation, (int)release.StationNodeId);
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
    public void Unit_status_carries_the_weapon_line_fire_mode_use_verdict_spread_and_warnings()
    {
        using var h = new Stage();
        var e = h.Play();
        var rifleman = new Fdp.Interfaces.TkbTemplate("test rifleman", 301);
        rifleman.AddDescriptor(new Fdp.Toolkit.Tkb.Domain.WeaponSuiteDto
        {
            Mounts =
            {
                new Fdp.Toolkit.Tkb.Domain.WeaponMountDto { Category = Fdp.Toolkit.Tkb.Domain.WeaponCategory.SmallArms, AmmoGuid = 6100 },
                new Fdp.Toolkit.Tkb.Domain.WeaponMountDto { Category = Fdp.Toolkit.Tkb.Domain.WeaponCategory.Grenade, AmmoGuid = 6101 },
            },
        });
        h.TestTkb.Register(rifleman);
        h.TestTkb.Register(new Fdp.Interfaces.TkbTemplate("5.56 mm round", 6100));
        h.TestTkb.Register(new Fdp.Interfaces.TkbTemplate("M67 grenade", 6101));
        h.World.SetSingletonManaged<Fdp.Interfaces.ITkbDatabase>(h.TestTkb);
        h.World.SetComponent(e, new TkbIdentity { TkbType = new TkbTypeId(301) });
        void Reg<T>()
            where T : unmanaged
        {
            if (!h.World.IsComponentTypeRegistered<T>())
            {
                h.World.RegisterComponent<T>();
            }
        }

        Reg<WeaponAmmo>();
        Reg<OperatorUseStatus>();
        Reg<Fdp.Toolkit.Perception.Radar.RadarWarning>();
        Reg<MissileWarning>();
        h.World.AddComponent(e, new WeaponAmmo
        {
            Count = 2, Ammo0 = 30, Max0 = 210, Mag0 = 3, Load0 = 6100, LoadRounds0 = 25, Busy = 0b01, Hot = 0b01, HeatAtLastShot0 = 0.5f, LastShotAt0 = h.SimTime, Ammo1 = 2, Mag1 = -1, Load1 = 0,
            LoadRounds1 = -1,
        });
        h.World.AddComponent(e, new OperatorUseStatus { UseSeq = 9, Outcome = UseOutcome.Refused, Reason = UseReason.Locked });
        h.World.AddComponent(e, new Fdp.Toolkit.Perception.Radar.RadarWarning { Level = Fdp.Toolkit.Perception.Radar.RadarWarningLevel.Tracked, BearingDeg = 135f });
        h.World.AddComponent(e, new MissileWarning { At = new SimInstant(h.SimTime), BearingDeg = 270f, Seeker = Fdp.Toolkit.Tkb.Domain.GuidanceKind.InfraRed, TimeToImpactS = 30f });
        h.Station.Control(Frame() with { Mount = 1, FirePref = 0 });
        h.Steps(1);

        var unit = h.Station.Unit;

        Assert.Equal((byte)1, unit.ActiveMount);   // the last control frame's mount
        var rifle = unit.Ammo[0];
        Assert.Equal(("RIFLE", "5.56 mm", 6100L, 30, 210, 3, 25, true, true), (rifle.Weapon, rifle.AmmoName, rifle.AmmoTypeId, rifle.Rounds, rifle.Max, rifle.Magazine, rifle.LoadedRounds, rifle.Busy,
            rifle.Overheated));
        Assert.Equal(50, rifle.HeatPercent);
        var grenade = unit.Ammo[1];   // no loaded round: the mount's own
        Assert.Equal(("GRENADE", "M67 grenade", 6101L, 2, -1, -1), (grenade.Weapon, grenade.AmmoName, grenade.AmmoTypeId, grenade.Rounds, grenade.Magazine, grenade.LoadedRounds));
        Assert.False(grenade.Busy);
        Assert.Equal("Lock", unit.FireMode);   // the selected weapon is the thrown one
        Assert.Equal(Fdp.Toolkit.Combat.HitModel.Sigma(h.World, e, Fdp.Toolkit.Combat.CombatTkb.MountOf(h.World, e, 1), h.SimTime), unit.SpreadRad);
        Assert.Equal((9, "Refused", "Locked"), ((int)unit.UseSeq, unit.UseOutcome, unit.UseReason));
        Assert.Equal(("Tracked", 135f), (unit.RadarWarning, unit.RadarBearingDeg));
        Assert.True(unit.MissileLaunch);
        Assert.Equal((270f, "InfraRed"), (unit.MissileBearingDeg, unit.MissileSeeker));
        Assert.InRange(unit.MissileTimeToImpactS, 29f, 30f);

        // the rifle selected: sight; the missile warning runs out with its time to impact
        h.Station.Control(Frame() with { Mount = 0 });
        h.Steps(1);
        Assert.Equal(((byte)0, "Sight"), (h.Station.Unit.ActiveMount, h.Station.Unit.FireMode));
        h.World.SetComponent(e, new MissileWarning { At = new SimInstant(h.SimTime - 40.0), BearingDeg = 270f, Seeker = Fdp.Toolkit.Tkb.Domain.GuidanceKind.InfraRed, TimeToImpactS = 30f });
        h.Steps(1);
        Assert.False(h.Station.Unit.MissileLaunch);
        Assert.Equal((0f, string.Empty), (h.Station.Unit.MissileTimeToImpactS, h.Station.Unit.MissileSeeker));
    }

    [Fact]
    public void A_unit_without_the_hud_components_has_empty_hud_fields()
    {
        using var h = new Stage();
        h.Play();
        h.Steps(1);

        var unit = h.Station.Unit;

        Assert.Equal((string.Empty, string.Empty, string.Empty, string.Empty, string.Empty, false, 0f), (unit.UseLabel, unit.UseOutcome, unit.UseReason, unit.RadarWarning, unit.MissileSeeker,
            unit.MissileLaunch, unit.MissileTimeToImpactS));
        Assert.Equal((byte)0, unit.UseSeq);
        Assert.Empty(unit.Ammo);
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
        Assert.Equal([((byte)0, 77L, 30), ((byte)1, 0L, 5)], unit.Ammo.Select(a => (a.Mount, a.AmmoTypeId, a.Rounds)).ToArray());
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

    /// <summary>
    /// An in-place restore (checkpoint, keyframe, end of a dry run) brings back HROT's PossessedBy from the snapshot: a playable unit held by this node with nobody playing
    /// is released on the next live frames, and the sweep stops once none is held.
    /// </summary>
    [Fact]
    public void After_an_in_place_restore_a_unit_still_held_by_this_node_is_released()
    {
        using var h = new Stage();
        var e = h.Play();
        h.Events<ReleasePossessionEvent>();

        WorldEpoch.Restored(h.World);   // same world, new epoch: the restored world still says this node holds the unit
        h.Step();

        Assert.Equal(PlayerSlotState.Choosing, h.Station.Slot.State);
        var release = Assert.Single(h.Read<ReleasePossessionEvent>());
        Assert.Equal(e, release.Unit);
        Assert.Equal(SeatStation, (int)release.StationNodeId);

        h.Step();
        Assert.Empty(h.Read<ReleasePossessionEvent>());   // asked at most once a second while HROT has not applied it

        h.AdvanceMs(1_000);
        h.Step();
        Assert.Single(h.Read<ReleasePossessionEvent>());   // still held (time did not run): asked again

        h.Release(e);   // HROT applied it
        h.AdvanceMs(1_000);
        h.Steps(3);
        Assert.Empty(h.Read<ReleasePossessionEvent>());

        // the sweep is over: a later possession of the unit by a player is left alone
        Assert.True(h.Station.Join(new PlayerJoin(PlayerJoinKind.Unit, Unit, null)));
        h.Hold(e);
        h.AdvanceMs(5_000);
        h.Steps(2);
        Assert.Equal(PlayerSlotState.Playing, h.Station.Slot.State);
        Assert.Empty(h.Read<ReleasePossessionEvent>());
    }

    [Fact]
    public void A_unit_this_node_holds_without_a_player_is_not_swept()
    {
        // the editor operator possesses a playable unit (or no player plugin is installed): no player of this station joined it, so a boundary leaves it held
        using var h = new Stage();
        var e = h.AddPlayable();
        h.Hold(e);
        h.Steps(2);

        h.Events<ReleasePossessionEvent>();

        WorldEpoch.Restored(h.World);
        h.Step();   // the first live frame after the boundary is where the sweep asks
        Assert.Empty(h.Read<ReleasePossessionEvent>());

        h.AdvanceMs(1_000);
        h.Step();
        Assert.Empty(h.Read<ReleasePossessionEvent>());
    }

    [Fact]
    public void A_unit_held_by_another_station_is_not_swept()
    {
        using var h = new Stage();
        var e = h.AddPlayable();
        h.Hold(e, station: 9);
        h.Steps(2);

        WorldEpoch.Restored(h.World);
        h.Steps(3);

        Assert.Empty(h.Read<ReleasePossessionEvent>());
    }

    /// <summary>A world boundary with a gizmo view set: the view ends with the runtime, the projectors stop (the client re-sends its view to the next runtime).</summary>
    [Fact]
    public void A_world_boundary_ends_the_gizmo_view()
    {
        using var h = new Stage();
        h.Station.SetView(new GizmoViewRequest(["Contacts"], [], [], 60f));
        h.Steps(2);
        int runs = h.Station.Gizmos.Runs;
        Assert.True(runs > 0);

        WorldEpoch.Restored(h.World);
        h.Steps(5);

        Assert.Equal(runs, h.Station.Gizmos.Runs);
    }

    /// <summary>
    /// The node's teardown runs one farewell tick, whose plugin pumps still call the station (a client's pending control frame, a join, a leave). The station is
    /// detached first: nothing reaches HROT's world, and DIS's final Deactivated states still go out.
    /// </summary>
    [Fact]
    public void Node_teardown_with_a_pending_control_frame_touches_no_world_and_dis_still_says_goodbye()
    {
        var probe = new ProbePlugin();
        bool farewell = false;
        probe.OnPump = b =>
        {
            if (farewell)
            {
                b.PlayerStation!.Control(Frame());
                b.PlayerStation.Join(new PlayerJoin(PlayerJoinKind.Unit, Unit, null));
                b.PlayerStation.Leave();
            }
        };
        using var h = new Stage(probe);
        h.Play();
        h.Events<OperatorControlFrameEvent>();
        h.DisSent.Clear();

        farewell = true;
        h.System.Dispose();

        h.World.Bus.SwapBuffers();
        Assert.Empty(h.Read<OperatorControlFrameEvent>());
        Assert.Empty(h.Read<PossessRequestEvent>());
        Assert.Empty(h.Read<ReleasePossessionEvent>());
        Assert.Empty(h.Errors);
        h.Drain();
        Assert.Contains(h.Sent<EntityStatePdu>(), p => SimNetGateway.Protocol.Dis7.EntityAppearance.GetDeactivated(p.EntityAppearance));
    }

    // ---- seats (DD-023): one HrotPlayerStation per seat, station node | n << 16 -------------------------------------------------------

    private static int SeatStationOf(int seat) => PackHarness.LocalNodeId | (seat << 16);

    /// <summary>A host with several seats; the "possession system" is the test's arbiter (first request in event order wins a free unit), as <see cref="Stage"/>'s Hold does for one.</summary>
    private sealed class SeatStage : PackHarness
    {
        public SeatStage(int seats, CountingDdsPlugin? plugin = null, string partition = "seats")
            : base(dis: false, plugins: plugin is null ? null : [plugin],
                configure: plugin is null ? null : c => c.WithSide(DpsSide.SideKey, new DpsSettings { Domain = 96, Partition = partition, Seats = seats }), seats: seats)
        {
            RegisterTemplate(300, 3);
            Steps(3);
        }

        public Dictionary<int, Entity> Units { get; } = new();

        public HrotPlayerStation Seat(int n) => System.PlayerSeats[n - 1];

        public Entity AddPlayable(int id)
        {
            var e = AddHrotEntity(id, new Vector3(1, 2, 0), tkb: 300);
            World.AddComponent(e, new Hrot.Common.Playable { Side = ForceId.Friend, SlotName = new FixedString32("Rifleman") });
            Units[id] = e;
            return e;
        }

        public int? HolderOf(int unit) => PossessionSystem.HolderOf(World, Units[unit]);

        private void Hold(Entity e, int station)
        {
            if (!World.IsComponentTypeRegistered<PossessedBy>())
            {
                World.RegisterComponent<PossessedBy>();
            }

            var held = new PossessedBy { StationNodeId = (StationId)station, Mode = PossessionMode.Fps, Epoch = 1 };
            if (World.HasComponent<PossessedBy>(e))
            {
                World.SetComponent(e, held);
            }
            else
            {
                World.AddComponent(e, held);
            }
        }

        /// <summary>One frame, then what HROT's possession system would do with the requests it published: a free unit goes to the first asker, a holder's release frees it.</summary>
        public void Turn()
        {
            Step();
            if (World.Bus.IsRegistered<PossessRequestEvent>())
            {
                foreach (ref readonly var r in World.ReadEvents<PossessRequestEvent>())
                {
                    if (World.IsAlive(r.Unit) && PossessionSystem.HolderOf(World, r.Unit) is null)
                    {
                        Hold(r.Unit, (int)r.StationNodeId);
                    }
                }
            }

            if (World.Bus.IsRegistered<ReleasePossessionEvent>())
            {
                foreach (ref readonly var r in World.ReadEvents<ReleasePossessionEvent>())
                {
                    if (World.IsAlive(r.Unit) && PossessionSystem.HolderOf(World, r.Unit) == (int)r.StationNodeId && World.HasComponent<PossessedBy>(r.Unit))
                    {
                        World.RemoveComponent<PossessedBy>(r.Unit);
                    }
                }
            }
        }

        public void Turns(int n)
        {
            for (int i = 0; i < n; i++)
            {
                Turn();
            }
        }
    }

    /// <summary>Seat n of a gateway host is the station node | n &lt;&lt; 16, whatever the number of seats; the node's own station (the editor user) is nobody's seat (D-11).</summary>
    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(4)]
    public void Each_seat_is_the_station_node_or_n_shifted_16(int seats)
    {
        using var h = new SeatStage(seats);

        Assert.Equal(seats, h.System.PlayerSeats.Count);
        for (int n = 1; n <= seats; n++)
        {
            Assert.Equal(PackHarness.LocalNodeId | (n << 16), h.Seat(n).Station);
            Assert.NotEqual(PackHarness.LocalNodeId, h.Seat(n).Station);
        }

        Assert.Same(h.System.PlayerStation, h.Seat(1));
    }

    /// <summary>The host offers the seats the running dps side asks for (<c>dps.seats</c>), at least one.</summary>
    [Fact]
    public void The_host_offers_as_many_seats_as_the_running_dps_side_asks_for()
    {
        var plugins = PluginSet.Of(new DdsPlayerPlugin());
        var config = GatewayConfiguration.CreateDefault(1) with { Dis = null };
        Assert.Equal(1, plugins.SeatsFor(config));
        Assert.Equal(3, plugins.SeatsFor(config.WithSide(DpsSide.SideKey, new DpsSettings { Seats = 3 })));
        Assert.Equal(1, plugins.SeatsFor(config.WithSide(DpsSide.SideKey, new DpsSettings { Seats = 3, Enabled = false })));
    }

    /// <summary>(DD-023 c) Two seats join one placed unit in the same frame: HROT's arbiter grants one; exactly one is Playing, the other is back to Choosing with HROT's refusal text as the reason.</summary>
    [Fact]
    public void Two_seats_joining_one_unit_in_one_frame_leave_exactly_one_playing_and_the_other_refused_with_hrots_text()
    {
        var plugin = new CountingDdsPlugin();
        using var losers = new SlotWatch(96, "race-2");
        using var h = new SeatStage(2, plugin, "race");
        h.AddPlayable(2001);
        h.Turns(2);

        Assert.True(h.Seat(1).Join(new PlayerJoin(PlayerJoinKind.Unit, 2001, null)));
        Assert.True(h.Seat(2).Join(new PlayerJoin(PlayerJoinKind.Unit, 2001, null)));   // nothing holds it yet: both are asked
        h.Turns(3);

        Assert.Equal([PlayerSlotState.Playing, PlayerSlotState.Choosing], [h.Seat(1).Slot.State, h.Seat(2).Slot.State]);
        Assert.Equal(SeatStationOf(1), h.HolderOf(2001));
        Assert.Contains($"station {SeatStationOf(1)}", h.Seat(2).Slot.Reason ?? string.Empty);
        Assert.Equal(0, h.Seat(2).Slot.UnitId);
        Assert.True(Wait(() =>
        {
            h.Turn();
            losers.Pump();
            return losers.Last is { } s && s.State == D.SlotState.SlotStateChoosing && s.Reason == Wire(h.Seat(2).Slot.Reason);   // the client of seat 2 is told, on its own partition
        }));
    }

    /// <summary>A seat that is refused outright (the unit is already held by another seat) hears HROT's text too, and a later successful join clears it.</summary>
    [Fact]
    public void A_join_refused_at_once_carries_hrots_text_and_a_later_join_clears_it()
    {
        using var h = new SeatStage(2);
        h.AddPlayable(2001);
        h.AddPlayable(2002);
        h.Turns(2);
        Assert.True(h.Seat(1).Join(new PlayerJoin(PlayerJoinKind.Unit, 2001, null)));
        h.Turns(3);
        Assert.Equal(PlayerSlotState.Playing, h.Seat(1).Slot.State);

        Assert.False(h.Seat(2).Join(new PlayerJoin(PlayerJoinKind.Unit, 2001, null)));
        Assert.Contains($"station {SeatStationOf(1)}", h.Seat(2).Slot.Reason ?? string.Empty);

        Assert.True(h.Seat(2).Join(new PlayerJoin(PlayerJoinKind.Unit, 2002, null)));
        h.Turns(3);
        Assert.Equal((PlayerSlotState.Playing, string.Empty), (h.Seat(2).Slot.State, h.Seat(2).Slot.Reason ?? string.Empty));
    }

    /// <summary>What the client sees of a reason: PlayerSlot.Reason is at most 64 characters.</summary>
    private static string Wire(string? reason) => reason is null ? string.Empty : reason.Length <= 64 ? reason : reason[..64];

    private static bool Wait(Func<bool> condition, int timeoutMs = 6000)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < timeoutMs)
        {
            if (condition())
            {
                return true;
            }

            Thread.Sleep(5);
        }

        return condition();
    }

    /// <summary>The newest PlayerSlot a client of one partition sees.</summary>
    private sealed class SlotWatch : IDisposable
    {
        private readonly DdsParticipant _participant;
        private readonly DdsReader<D.PlayerSlot> _reader;

        public SlotWatch(uint domain, string partition)
        {
            _participant = new DdsParticipant(domain, defaultPartition: partition);
            _reader = new DdsReader<D.PlayerSlot>(_participant);
        }

        public D.PlayerSlot? Last { get; private set; }

        public void Pump()
        {
            using var loan = _reader.Take(64);
            foreach (var sample in loan)
            {
                if (sample.IsValid && sample.Info.InstanceState == DdsInstanceState.Alive)
                {
                    Last = sample.Data;
                }
            }
        }

        public void Dispose()
        {
            _reader.Dispose();
            _participant.Dispose();
        }
    }

    /// <summary>
    /// (DD-023 b) Seats are independent: seeded random joins, controls and leaves on 1 .. 4 seats over real DDS. After every step each client's PlayerSlot equals its seat station's slot, no unit
    /// is Playing on two seats, a Playing seat is the HROT holder of its unit, and no seat ever holds a unit another seat's station holds.
    /// </summary>
    [Theory]
    [InlineData(1, 1)]
    [InlineData(2, 2)]
    [InlineData(3, 3)]
    [InlineData(4, 4)]
    [InlineData(5, 3)]
    [InlineData(6, 2)]
    public void Seats_are_independent_over_random_joins_controls_and_leaves(int seed, int seats)
    {
        var rng = new Random(seed);
        string partition = "prop" + seed;
        // one seat = the base partition itself, several = base-n
        var watches = Enumerable.Range(1, seats).Select(n => new SlotWatch(96, seats == 1 ? partition : $"{partition}-{n}")).ToArray();
        var plugin = new CountingDdsPlugin();
        using var h = new SeatStage(seats, plugin, partition);
        try
        {
            for (int u = 0; u <= seats; u++)
            {
                h.AddPlayable(2001 + u);   // one more unit than seats: contention, but a free one too
            }

            h.Turns(3);

            void Settle()
            {
                Assert.True(Wait(() =>
                {
                    h.Turn();
                    foreach (var w in watches)
                    {
                        w.Pump();
                    }

                    for (int n = 1; n <= seats; n++)
                    {
                        var slot = h.Seat(n).Slot;
                        if (watches[n - 1].Last is not { } seen || (byte)seen.State != (byte)slot.State || seen.UnitId != slot.UnitId || seen.Reason != Wire(slot.Reason))
                        {
                            return false;
                        }
                    }

                    return true;
                }), "every client's PlayerSlot reaches its seat's slot");
            }

            Settle();
            for (int step = 0; step < 24; step++)
            {
                int n = 1 + rng.Next(seats);
                var seat = h.Seat(n);
                switch (seat.Slot.State)
                {
                    case PlayerSlotState.Choosing when seat.Offers.Count > 0:
                        seat.Join(new PlayerJoin(PlayerJoinKind.Unit, seat.Offers[rng.Next(seat.Offers.Count)].UnitId, null));
                        break;
                    case PlayerSlotState.Playing when rng.Next(2) == 0:
                        seat.Leave();
                        break;
                    case PlayerSlotState.Playing:
                        seat.Control(Frame());
                        break;
                }

                if (rng.Next(3) == 0)
                {
                    // the contested case: another seat asks for the same unit in the same frame
                    int other = 1 + rng.Next(seats);
                    var o = h.Seat(other);
                    if (other != n && o.Slot.State == PlayerSlotState.Choosing && seat.Slot.UnitId != 0)
                    {
                        o.Join(new PlayerJoin(PlayerJoinKind.Unit, seat.Slot.UnitId, null));
                    }
                }

                Settle();

                var playing = new Dictionary<int, int>();
                for (int k = 1; k <= seats; k++)
                {
                    var st = h.Seat(k);
                    Assert.Equal(SeatStationOf(k), st.Station);
                    if (st.Slot.State == PlayerSlotState.Playing)
                    {
                        Assert.True(playing.TryAdd(st.Slot.UnitId, k), $"unit {st.Slot.UnitId} is Playing on two seats");
                        Assert.Equal(SeatStationOf(k), h.HolderOf(st.Slot.UnitId));
                    }
                }

                foreach (var id in h.Units.Keys)
                {
                    if (h.HolderOf(id) is { } holder && holder != PackHarness.LocalNodeId)
                    {
                        Assert.Contains(Enumerable.Range(1, seats), k => SeatStationOf(k) == holder);   // only a seat of this gateway holds a unit, and just one
                    }
                }
            }
        }
        finally
        {
            foreach (var w in watches)
            {
                w.Dispose();
            }
        }
    }

    // ---- T-054: PlayerMessage per seat (DD-023 D-15) ----------------------------------------------------------------------------------

    private sealed class MessageWatch : IDisposable
    {
        private readonly DdsParticipant _participant;
        private readonly DdsReader<D.PlayerMessage> _reader;

        public MessageWatch(uint domain, string partition)
        {
            _participant = new DdsParticipant(domain, defaultPartition: partition);
            _reader = new DdsReader<D.PlayerMessage>(_participant);
        }

        public HashSet<int> Ids { get; } = new();

        public void Pump()
        {
            using var loan = _reader.Take(64);
            foreach (var sample in loan)
            {
                if (sample.IsValid)
                {
                    Ids.Add(sample.Data.MessageId);
                }
            }
        }

        public void Dispose()
        {
            _reader.Dispose();
            _participant.Dispose();
        }
    }

    /// <summary>
    /// T-054: every address kind reaches exactly the seats HROT's own rule (<c>PlayerMessageAddressing.Addressed</c>) picks over each seat's viewer - the unit it holds, its side, its station - and only those, on the seat's own
    /// participant. Seat 1 holds a Friend unit, seat 2 a Hostile one, seat 3 holds nothing (a seat with no unit is still addressed by All, by its station and, being side-less, by the neutral side).
    /// Mutations: the message written on the world participant (every seat hears it); the station-addressed one delivered to every seat.
    /// </summary>
    [Fact]
    public void Each_address_kind_reaches_exactly_the_seats_hrots_rule_picks()
    {
        using var w1 = new MessageWatch(96, "msg-1");
        using var w2 = new MessageWatch(96, "msg-2");
        using var w3 = new MessageWatch(96, "msg-3");
        using var h = new SeatStage(3, new CountingDdsPlugin(), "msg");
        h.AddPlayable(2001);
        var hostile = h.AddPlayable(2002);
        h.World.SetComponent(hostile, new Hrot.Common.Playable { Side = ForceId.Hostile, SlotName = new FixedString32("Gunner") });
        h.Turns(2);
        Assert.True(h.Seat(1).Join(new PlayerJoin(PlayerJoinKind.Unit, 2001, null)));
        Assert.True(h.Seat(2).Join(new PlayerJoin(PlayerJoinKind.Unit, 2002, null)));
        h.Turns(4);
        Assert.Equal([PlayerSlotState.Playing, PlayerSlotState.Playing, PlayerSlotState.Choosing], [h.Seat(1).Slot.State, h.Seat(2).Slot.State, h.Seat(3).Slot.State]);

        var viewers = new[]
        {
            new Hrot.Common.Messages.MessageViewer(2001, ForceId.Friend, (StationId)SeatStationOf(1)),
            new Hrot.Common.Messages.MessageViewer(2002, ForceId.Hostile, (StationId)SeatStationOf(2)),
            new Hrot.Common.Messages.MessageViewer(0, ForceId.Neutral, (StationId)SeatStationOf(3)),
        };
        // (id, kind, the value on the wire: a side is a DIS force; the rule's value: HROT's force id)
        var cases = new (int Id, string Kind, int Wire, int Rule)[]
        {
            (11, "All", 0, 0), (12, "Side", 1, (int)ForceId.Friend), (13, "Side", 2, (int)ForceId.Hostile), (14, "Side", 3, (int)ForceId.Neutral),
            (15, "Unit", 2002, 2002), (16, "Station", SeatStationOf(3), SeatStationOf(3)), (17, "Station", SeatStationOf(1), SeatStationOf(1)), (18, "Instructor", 0, 0),
        };
        var watches = new[] { w1, w2, w3 };
        bool Done() => cases.All(c => viewers.Select((v, i) => (v, i)).All(t => !Hrot.Common.Messages.PlayerMessageAddressing.Addressed(c.Kind, c.Rule, t.v) || watches[t.i].Ids.Contains(c.Id)));
        Assert.True(Wait(() =>
        {
            foreach (var c in cases)
            {
                h.World.Bus.PublishManaged(new Fdp.Toolkit.ScenarioRules.PlayerMessageEvent { MessageId = c.Id, ToKind = c.Kind, ToValue = c.Kind == "Side" ? c.Rule : c.Wire, Text = "m" + c.Id });
            }

            h.World.Bus.SwapBuffers();
            h.Turn();
            foreach (var w in watches)
            {
                w.Pump();
            }

            return Done();
        }, 15000));
        h.Turns(3);
        foreach (var w in watches)
        {
            w.Pump();
        }

        for (int seat = 0; seat < 3; seat++)
        {
            var expected = cases.Where(c => Hrot.Common.Messages.PlayerMessageAddressing.Addressed(c.Kind, c.Rule, viewers[seat])).Select(c => c.Id).OrderBy(x => x).ToArray();
            Assert.Equal(expected, watches[seat].Ids.OrderBy(x => x).ToArray());
        }

        Assert.Equal([11, 12, 17], watches[0].Ids.OrderBy(x => x).ToArray());   // seat 1: All, its side, its station
        Assert.Equal([11, 13, 15], watches[1].Ids.OrderBy(x => x).ToArray());                        // seat 2: All, its side, its unit
        Assert.Equal([11, 14, 16], watches[2].Ids.OrderBy(x => x).ToArray());                        // seat 3: All, the neutral side, its station; the instructor message nobody
    }
}
