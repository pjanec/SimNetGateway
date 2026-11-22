using System.Numerics;
using CarKinem.Core;
using Fdp.Core;
using Fdp.Core.CommandHierarchy;
using Fdp.Interfaces;
using Fdp.Toolkit.Behavior.Components;
using Fdp.Toolkit.Civilians;
using Fdp.Toolkit.Combat;
using Fdp.Toolkit.Combat.Components;
using Fdp.Toolkit.Creatures;
using Fdp.Toolkit.Replication.Components;
using GizmoMap.Contracts.Symbology;
using Hrot.Common.Diagnostics.Gizmos;
using Hrot.Core.Tkb;
using SimNetGateway.Engine;
using SimNetGateway.Engine.InMemory;
using SimNetGateway.Protocol.Hrot;

namespace SimNetGateway.HrotHost.Tests;

/// <summary>
/// T-062 (DD-025): what a client draws an entity's card from, read from HROT's own rules (the expected values below are COMPUTED by HROT's functions, never typed in): the symbol, the health pair,
/// a unit's strength and ammunition share, a man's mind-state bar; only for entities HROT would give a card; sent on change at a step of 0.01; 0 B per steady gather.
/// </summary>
public class EntityStatusGatherTests
{
    private const long ManType = 300, TankType = 310, CivilianType = 320, SquadType = 400;
    private const int Man = 2001, Tank = 2002, Civilian = 2003, Squad = 2004, FirstMember = 2101;

    private sealed class Capture : IIngressSink
    {
        public readonly List<object> All = new();

        public void Deliver<T>(in T message, double? receivedAt = null) => All.Add(message!);

        public List<T> Of<T>() => All.OfType<T>().ToList();
    }

    private sealed class Stage : PackHarness
    {
        private double _now = 1000;
        private double _sim;

        public Stage()
            : base(dis: true)
        {
            RegisterTemplate(ManType, 3);
            RegisterTemplate(TankType, 1);
            RegisterTemplate(CivilianType, 3);
            TestTkb.Register(new TkbTemplate("squad", SquadType) { DisType = UnitDis.Land(UnitArm.Infantry, UnitEchelon.Squad, 225) });
            World.SetSingletonManaged<ITkbDatabase>(TestTkb);
            Ensure<UnitEntity>();
            Ensure<UnitRoster>();
            Ensure<Stress>();
            Ensure<VehicleParams>();
            Ensure<Fear>();
            Ensure<CivilianState>();
            Ensure<WeaponAmmo>();
            Ensure<AreaEffectCloud>();
            Steps(3);
        }

        private void Ensure<T>()
            where T : unmanaged
        {
            if (!World.IsComponentTypeRegistered<T>())
            {
                World.RegisterComponent<T>();
            }
        }

        public Capture Gather()
        {
            var inbound = new InMemoryInbound(new SourceId(99), "test");
            _now += 0.1;
            _sim += 0.1;
            Adapter.GatherEntities(World, inbound, _now, _sim);
            var sink = new Capture();
            inbound.Drain(sink, int.MaxValue);
            return sink;
        }

        public Entity AddMan(int id, float health = 60f, float stress = 0.5f, ForceId force = ForceId.Friend)
        {
            var e = AddHrotEntity(id, new Vector3(id % 100, 0, 0), "man" + id, ManType, force, health);
            World.SetComponent(e, new Health { Current = health, Max = 100f });
            World.AddComponent(e, new VehicleParams { Class = VehicleClass.Pedestrian });
            World.AddComponent(e, new Stress { Level = stress });
            return e;
        }

        /// <summary>A squad commander: a body-less unit with a roster of four men, one of them dead, each carrying 30 of 60 rounds.</summary>
        public Entity AddSquad(int id, ForceId force = ForceId.Friend)
        {
            var roster = default(UnitRoster);
            for (int i = 0; i < 4; i++)
            {
                var member = AddHrotEntity(FirstMember + i, new Vector3(5 + i, 0, 0), "member" + i, ManType, force, i == 3 ? 0f : 100f);
                World.AddComponent(member, new WeaponAmmo { Count = 1, Ammo0 = 30, Max0 = 60 });
                UnitRoster.Add(ref roster, member);
            }

            var squad = AddHrotEntity(id, new Vector3(6, 0, 0), "squad", SquadType, force);
            World.RemoveComponent<Health>(squad);   // a unit has no health
            World.AddComponent(squad, new UnitEntity());
            World.AddComponent(squad, roster);
            return squad;
        }
    }

    private static HrotEntityStatus StatusOf(Capture c, int id) => Assert.Single(c.Of<HrotEntityStatus>().Where(s => s.Id == id));

    private static string Wire(Stage h, long type, ForceId side)
    {
        Assert.True(UnitSymbolDeriver.TryOfType(h.TestTkb, type, out var derived));
        var symbol = UnitSymbolDeriver.WithSide(derived, side);
        Span<char> chars = stackalloc char[SymbolCode.MaxLength];
        return new string(chars[..symbol.Format(chars)]);
    }

    // ---- each field equals HROT's rule's output ---------------------------------------------------------------------------------------

    [Fact]
    public void A_man_has_the_health_pair_and_the_mind_state_bar_HROT_computes_and_no_unit_fields()
    {
        using var h = new Stage();
        var man = h.AddMan(Man, health: 60f, stress: 0.5f);
        var (fill, tick, band) = MindStateBar.Of(h.World, man);
        Assert.NotEqual(MindBand.None, band);   // the fixture really has a bar to compare

        var s = StatusOf(h.Gather(), Man);

        Assert.Equal((60f, 100f), (s.HealthCurrent, s.HealthMax));   // the PAIR, never a fraction
        Assert.Equal((fill, tick, band.ToString()), (s.MindFill, s.MindBreak, s.MindBand.ToString()));
        Assert.Equal((string.Empty, 0, 0, -1f), (s.Symbol, s.StrengthPresent, s.StrengthAuthorized, s.AmmoShare));   // a man is not a unit
    }

    [Fact]
    public void A_vehicle_has_its_health_pair_and_nothing_else()
    {
        using var h = new Stage();
        var tank = h.AddHrotEntity(Tank, new Vector3(3, 3, 0), "tank", TankType, ForceId.Hostile, 30f);
        h.World.SetComponent(tank, new Health { Current = 30f, Max = 200f });

        var s = StatusOf(h.Gather(), Tank);

        Assert.Equal((30f, 200f, string.Empty, 0, 0, -1f, 0f, -1f, HrotMindBand.None), (s.HealthCurrent, s.HealthMax, s.Symbol, s.StrengthPresent, s.StrengthAuthorized, s.AmmoShare, s.MindFill, s.MindBreak, s.MindBand));
    }

    [Fact]
    public void A_squad_commander_has_the_symbol_strength_and_ammunition_share_of_HROTs_rules_and_no_health()
    {
        using var h = new Stage();
        var squad = h.AddSquad(Squad, ForceId.Friend);
        var (present, authorized, _, known) = UnitCardFacts.Strength(h.World, squad);
        float ammo = UnitCardFacts.AmmoShare(h.World, squad);
        Assert.True(known);
        Assert.Equal(4, authorized);
        Assert.InRange(present, 1, 3);   // the dead man is not present
        Assert.False(float.IsNaN(ammo));

        var s = StatusOf(h.Gather(), Squad);

        Assert.Equal((present, authorized, ammo), (s.StrengthPresent, s.StrengthAuthorized, s.AmmoShare));
        Assert.Equal(Wire(h, SquadType, ForceId.Friend), s.Symbol);
        Assert.NotEqual(string.Empty, s.Symbol);
        Assert.Equal((0f, HrotMindBand.None), (s.HealthMax, s.MindBand));
        Assert.True(s.Symbol.Length <= 40);
    }

    [Fact]
    public void The_symbol_is_in_the_frame_of_the_units_side()
    {
        using var h = new Stage();
        var squad = h.AddSquad(Squad, ForceId.Friend);
        string friend = StatusOf(h.Gather(), Squad).Symbol;

        h.World.SetComponent(squad, new EntityInfo { Name = new FixedString64("squad"), ForceId = ForceId.Hostile });
        string hostile = StatusOf(h.Gather(), Squad).Symbol;   // a change of side is a changed symbol, sent once

        Assert.NotEqual(friend, hostile);
        Assert.Equal(Wire(h, SquadType, ForceId.Hostile), hostile);
    }

    [Fact]
    public void A_civilian_has_the_fear_bar_HROT_computes()
    {
        using var h = new Stage();
        var civilian = h.AddHrotEntity(Civilian, new Vector3(9, 9, 0), "civilian", CivilianType, ForceId.Neutral, 100f);
        h.World.AddComponent(civilian, new VehicleParams { Class = VehicleClass.Pedestrian });
        h.World.AddComponent(civilian, new CivilianState());
        h.World.AddComponent(civilian, new Fear { Value = 0.6f });
        var (fill, tick, band) = MindStateBar.Of(h.World, civilian);
        Assert.Equal(MindBand.Fear, band);

        var s = StatusOf(h.Gather(), Civilian);

        Assert.Equal((fill, tick, HrotMindBand.Fear), (s.MindFill, s.MindBreak, s.MindBand));
        Assert.Equal((100f, 100f), (s.HealthCurrent, s.HealthMax));
    }

    [Fact]
    public void An_entity_with_no_health_has_a_zero_maximum_and_a_dead_man_has_no_mind_bar()
    {
        using var h = new Stage();
        var man = h.AddMan(Man);
        h.World.RemoveComponent<Health>(man);
        var s = StatusOf(h.Gather(), Man);
        Assert.Equal((0f, 0f), (s.HealthCurrent, s.HealthMax));

        h.World.AddComponent(man, new Health { Current = 0f, Max = 100f });   // dead: HROT's gizmo draws no bar for a destroyed man
        var dead = StatusOf(h.Gather(), Man);
        Assert.Equal((0f, 100f, HrotMindBand.None), (dead.HealthCurrent, dead.HealthMax, dead.MindBand));
    }

    // ---- only the entities HROT gives a card -------------------------------------------------------------------------------------------

    [Fact]
    public void An_entity_with_no_card_gets_no_status_and_one_that_loses_its_card_is_withdrawn_once()
    {
        using var h = new Stage();
        h.AddMan(Man);
        var rule = h.AddHrotEntity(2050, new Vector3(1, 1, 0), "objective", Hrot.Map.Common.TkbEntityTypes.Objective);   // a rule entity: a brain with no body
        var cloud = h.AddHrotEntity(2051, new Vector3(2, 2, 0), "cloud", ManType);
        h.World.AddComponent(cloud, new AreaEffectCloud());   // drawn by its own layer
        var withCard = h.AddHrotEntity(2052, new Vector3(3, 3, 0), "later", ManType);

        var first = h.Gather();
        Assert.Equal([Man, 2052], first.Of<HrotEntityStatus>().Select(s => s.Id).Order().ToArray());
        Assert.Contains(first.Of<HrotEntityState>(), s => s.Id == rule.Index || s.Id == 2050);   // the entity IS published: only its card is not

        h.World.AddComponent(withCard, new AreaEffectCloud());   // it becomes something that has no card
        var second = h.Gather();
        Assert.Equal([2052], second.Of<HrotEntityStatusRemoved>().Select(s => s.Id).ToArray());
        Assert.Empty(second.Of<HrotEntityStatus>());
        Assert.Empty(h.Gather().Of<HrotEntityStatusRemoved>());   // once
    }

    [Fact]
    public void An_entity_that_leaves_and_comes_back_with_the_same_id_is_sent_again()
    {
        using var h = new Stage();
        var man = h.AddMan(Man);
        StatusOf(h.Gather(), Man);
        h.RemoveHrotEntity(man);
        Assert.Contains(h.Gather().Of<HrotEntityRemoved>(), r => r.Id == Man);

        h.AddMan(Man);
        StatusOf(h.Gather(), Man);
    }

    // ---- on change, quantised ----------------------------------------------------------------------------------------------------------

    [Fact]
    public void A_steady_world_writes_nothing_a_health_change_of_half_a_step_nothing_and_of_two_steps_once()
    {
        using var h = new Stage();
        var man = h.AddMan(Man, health: 60f);
        h.AddSquad(Squad);
        Assert.Equal(6, h.Gather().Of<HrotEntityStatus>().Count);   // the man, the squad and its four members
        for (int i = 0; i < 5; i++)
        {
            Assert.Empty(h.Gather().Of<HrotEntityStatus>());   // a steady world
        }

        h.World.SetComponent(man, new Health { Current = 59.995f, Max = 100f });   // 0.005
        Assert.Empty(h.Gather().Of<HrotEntityStatus>());

        h.World.SetComponent(man, new Health { Current = 59.98f, Max = 100f });   // 0.02 from what was SENT (60), not from the last gather
        var s = Assert.Single(h.Gather().Of<HrotEntityStatus>());
        Assert.Equal((Man, 59.98f), (s.Id, s.HealthCurrent));
        Assert.Empty(h.Gather().Of<HrotEntityStatus>());

        h.World.SetComponent(man, new Health { Current = 59.975f, Max = 100f });   // drift under the step from the sent value never sends
        Assert.Empty(h.Gather().Of<HrotEntityStatus>());
    }

    [Fact]
    public void A_mind_band_change_strength_change_and_ammunition_change_each_send_once()
    {
        using var h = new Stage();
        var man = h.AddMan(Man, stress: 0.5f);
        var squad = h.AddSquad(Squad);
        var first = h.Gather();
        var before = StatusOf(first, Man);
        var squadBefore = StatusOf(first, Squad);

        h.World.SetComponent(man, new Stress { Level = 1f, Broken = 1 });   // calm / nervous -> broken
        var (_, _, band) = MindStateBar.Of(h.World, man);
        Assert.Equal(MindBand.Broken, band);
        var broken = StatusOf(h.Gather(), Man);
        Assert.NotEqual(before.MindBand, broken.MindBand);
        Assert.Equal(HrotMindBand.Broken, broken.MindBand);
        Assert.Empty(h.Gather().Of<HrotEntityStatus>());

        var roster = h.World.GetComponentRO<UnitRoster>(squad);
        h.World.SetComponent(roster.SubordinateEntities[0], new Health { Current = 0f, Max = 100f });   // another man falls
        var (present, _, _, _) = UnitCardFacts.Strength(h.World, squad);
        var after = StatusOf(h.Gather(), Squad);
        Assert.Equal(present, after.StrengthPresent);
        Assert.NotEqual(squadBefore.StrengthPresent, after.StrengthPresent);

        h.World.SetComponent(roster.SubordinateEntities[1], new WeaponAmmo { Count = 1, Ammo0 = 0, Max0 = 60 });   // a man fires his last round
        var ammo = StatusOf(h.Gather(), Squad);
        Assert.Equal(UnitCardFacts.AmmoShare(h.World, squad), ammo.AmmoShare);
        Assert.NotEqual(after.AmmoShare, ammo.AmmoShare);
    }

    // ---- HOT path ----------------------------------------------------------------------------------------------------------------------

    /// <summary>A steady gather of the status allocates nothing (the symbol string is made once per type and side).</summary>
    [Fact]
    public void A_steady_status_gather_allocates_nothing()
    {
        using var h = new Stage();
        h.AddMan(Man);
        h.AddSquad(Squad);
        var civilian = h.AddHrotEntity(Civilian, new Vector3(9, 9, 0), "civilian", CivilianType, ForceId.Neutral, 100f);
        h.World.AddComponent(civilian, new VehicleParams { Class = VehicleClass.Pedestrian });
        h.World.AddComponent(civilian, new CivilianState());
        h.World.AddComponent(civilian, new Fear { Value = 0.6f });
        h.AddHrotEntity(Tank, new Vector3(3, 3, 0), "tank", TankType, ForceId.Hostile, 30f);
        var inbound = new InMemoryInbound(new SourceId(99), "test");
        var status = new EntityStatusGather();
        var tkb = h.TestTkb;
        var query = h.World.Query().With<NetworkIdentity>().With<TkbIdentity>().Build();
        void Pass()
        {
            foreach (var e in query)
            {
                status.Gather(h.World, tkb, e, h.World.GetComponentRO<NetworkIdentity>(e).Value, h.World.GetComponentRO<TkbIdentity>(e).TkbType.Value, inbound);
            }
        }

        for (int i = 0; i < 100; i++)
        {
            Pass();
        }

        Thread.Sleep(250);   // past the runtime's tier-up delay, so the measured passes run the final code
        for (int i = 0; i < 100; i++)
        {
            Pass();
        }

        inbound.Drain(new Capture(), int.MaxValue);
        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 300; i++)
        {
            Pass();
        }

        Assert.Equal(0L, GC.GetAllocatedBytesForCurrentThread() - before);
    }

    // ---- through the host and the dps plugin ---------------------------------------------------------------------------------------------

    /// <summary>The whole chain: the gather, the pivot message, the plugin's module and the world participant's writer - a steady world writes no status, a health change of two steps writes one, a removal ends it.</summary>
    [Fact]
    public void Through_the_host_and_the_plugin_a_status_is_written_once_per_change()
    {
        var plugin = new CountingDdsPlugin();
        var written = new List<(int Id, float Health)>();
        plugin.OnBound = t => t.EntityStatus.Tap = s => written.Add((s.EntityId, s.HealthCurrent));
        using var h = new PackHarness(dis: false, plugins: [plugin], configure: c => c.WithSide(Plugin.DdsPlayer.DpsSide.SideKey, new Plugin.DdsPlayer.DpsSettings { Domain = 98, Partition = "t62-status" }));
        h.RegisterTemplate(ManType, 3);
        h.Steps(3);
        var man = h.AddHrotEntity(Man, new Vector3(1, 2, 0), "man", ManType, ForceId.Friend, 100f);
        h.Steps(5);
        Assert.Equal([(Man, 100f)], written);   // once

        h.Steps(30);
        Assert.Single(written);   // a steady world

        h.World.SetComponent(man, new Health { Current = 99.995f, Max = 100f });
        h.Steps(5);
        Assert.Single(written);   // half a step

        h.World.SetComponent(man, new Health { Current = 99.98f, Max = 100f });
        h.Steps(5);
        Assert.Equal([(Man, 100f), (Man, 99.98f)], written);

        h.RemoveHrotEntity(man);
        h.Steps(5);
        Assert.Equal(2, written.Count);   // the removal disposes the instance, it writes no sample
    }
}
