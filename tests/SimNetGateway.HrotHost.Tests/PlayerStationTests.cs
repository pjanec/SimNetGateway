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
        Assert.Equal(PackHarness.LocalNodeId, (int)release.StationNodeId);

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
}
