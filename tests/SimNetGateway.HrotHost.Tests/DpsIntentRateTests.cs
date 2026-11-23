using System.Numerics;
using Fdp.Core;
using Fdp.Toolkit.Behavior.Components;
using Fdp.Toolkit.Combat.Components;
using Fdp.Toolkit.Perception.Look;
using Fdp.Toolkit.Tkb.Domain;
using SimNetGateway.Engine.InMemory;
using SimNetGateway.Protocol.Hrot;
using Hrot.MuscleCharacter.Animation.Components;
using SimNetGateway.Engine;
using SimNetGateway.Gateway.HrotDis;
using SimNetGateway.Plugin.DdsPlayer;
using Xunit.Abstractions;
using GatewayConfiguration = SimNetGateway.Gateway.HrotDis.GatewayConfiguration;

namespace SimNetGateway.HrotHost.Tests;

/// <summary>The dps plugin around a real <see cref="DdsPlayerPlugin"/> that keeps the transport, so a test can count what was written.</summary>
internal sealed class CountingDdsPlugin : IGatewayPlugin
{
    private readonly DdsPlayerPlugin _inner = new();

    /// <summary>The plugin behind (a test sets its wall clock before the host services are made).</summary>
    public DdsPlayerPlugin Inner => _inner;

    public DpsTransport? Transport { get; private set; }

    /// <summary>Runs when the transport is bound, before the first frame writes (a test taps the channels then).</summary>
    public Action<DpsTransport>? OnBound { get; set; }

    public string Name => _inner.Name;

    public int InterfaceVersion => _inner.InterfaceVersion;

    public IProtocolSide Side => _inner.Side;

    public bool WantsEventsInReplay(object settings) => _inner.WantsEventsInReplay(settings);

    public int PlayerSeats(object settings) => _inner.PlayerSeats(settings);

    public string? Check(object settings, PluginHostContext context) => _inner.Check(settings, context);

    public IHostService? CreateHostService(object settings, PluginHostContext context) => _inner.CreateHostService(settings, context);

    public PluginBound Bind(SideBinding binding)
    {
        var bound = _inner.Bind(binding);
        Transport = (DpsTransport)bound.Lease!;
        OnBound?.Invoke(Transport);
        return bound;
    }
}

/// <summary>DD-020 3.2: a staged fight through the host and the dps plugin, counting the samples written for the people in it (the measurement of the as-built).</summary>
public class DpsIntentRateTests(ITestOutputHelper output)
{
    // DDS domains of the test assemblies are disjoint (Plugin 1..50, Protocol.DdsPlayer 55..94, HrotHost 96..99, DpsClient 100..166); all stay below domain 167
    private const int Domain = 97;

    private const int Men = 20, Steps = 600;   // 10 s at 60 frames a second

    /// <summary>20 men aim at 20 moving targets (5 m/s), their heads follow the aim, for 10 s; returns the PersonState (and PersonAim) samples written.</summary>
    internal static (int PersonState, int PersonAim, int EntityState) StagedFight(string partition, Action<Fdp.Core.EntityRepository, Entity, Entity, int>? extra = null)
    {
        var plugin = new CountingDdsPlugin();
        using var h = new PackHarness(dis: false, plugins: [plugin],
            configure: c => c.WithSide(DpsSide.SideKey, new DpsSettings { Domain = Domain, Partition = partition }));
        h.RegisterTemplate(100, 1);
        h.RegisterTemplate(300, 3);
        h.Steps(3);
        var shooters = new List<Entity>();
        var targets = new List<Entity>();
        for (int i = 0; i < Men; i++)
        {
            var shooter = h.AddHrotEntity(2000 + i, new Vector3(i * 10, 0, 0), tkb: 300);
            var target = h.AddHrotEntity(3000 + i, new Vector3(i * 10, 80, 0), tkb: 300, force: ForceId.Hostile);
            h.World.AddComponent(shooter, new StanceIntent { TargetStance = StanceId.Standing });
            h.World.AddComponent(shooter, new WeaponPosture { Kind = WeaponPostureKind.Aimed, Target = target, HeldItem = HeldItemKind.Weapon });
            h.World.AddComponent(shooter, new LookState { HasTarget = 0, HeadYaw = new Angle(0f) });
            shooters.Add(shooter);
            targets.Add(target);
        }

        for (int step = 0; step < Steps; step++)
        {
            double t = step / 60.0;
            for (int i = 0; i < Men; i++)
            {
                // the target walks sideways at 5 m/s; the head follows the aim (the bearing changes as it moves)
                float x = i * 10 + (float)(5.0 * t);
                h.Move(targets[i], new Vector3(x, 80, 0));
                float bearing = MathF.Atan2(80f, x - i * 10) - MathF.PI / 2f;
                h.World.SetComponent(shooters[i], new LookState { HasTarget = 0, HeadYaw = new Angle(bearing) });
                extra?.Invoke(h.World, shooters[i], targets[i], step);
            }

            h.Step();
        }

        var tr = plugin.Transport!;
        return (tr.PersonState.Written, PersonAimWritten(tr), tr.EntityState.Written);
    }

    private static int PersonAimWritten(DpsTransport t) => t.GetType().GetProperty("PersonAim", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance) is { } p
        ? (int)p.GetValue(t)!.GetType().GetProperty("Written")!.GetValue(p.GetValue(t))!
        : -1;

    // ---- the host's intent (DD-020 3.2) -----------------------------------------------------------------------------------------

    private sealed class Capture : IIngressSink
    {
        public readonly List<object> All = new();

        public void Deliver<T>(in T message, double? receivedAt = null) => All.Add(message!);

        public List<T> Of<T>() => All.OfType<T>().ToList();
    }

    private static double _now = 1000;

    private static Capture Gather(PackHarness h, double simTime)
    {
        var inbound = new InMemoryInbound(new SourceId(99), "test");
        _now += 0.1;
        h.Adapter.GatherEntities(h.World, inbound, _now, simTime);
        var sink = new Capture();
        inbound.Drain(sink, int.MaxValue);
        return sink;
    }

    private static PackHarness Live()
    {
        var h = new PackHarness(dis: true);
        h.RegisterTemplate(100, 1);
        h.RegisterTemplate(300, 3);
        h.Steps(3);
        return h;
    }

    private static Entity Soldier(PackHarness h, int id, Vector3 at)
    {
        var e = h.AddHrotEntity(id, at, tkb: 300);
        h.World.AddComponent(e, new StanceIntent { TargetStance = StanceId.Standing });
        h.World.AddComponent(e, new WeaponPosture { Kind = WeaponPostureKind.Ready, HeldItem = HeldItemKind.Weapon });
        return e;
    }

    [Fact]
    public void An_aim_at_a_unit_names_it_and_the_entity_state_still_follows_the_moving_chest()
    {
        using var h = Live();
        var target = h.AddHrotEntity(2002, new Vector3(40, 10, 0), tkb: 100);
        var shooter = Soldier(h, 2001, Vector3.Zero);
        h.World.SetComponent(shooter, new WeaponPosture { Kind = WeaponPostureKind.Aimed, Target = target, HeldItem = HeldItemKind.Weapon });

        var first = Assert.Single(Gather(h, 1.0).Of<HrotEntityState>(), s => s.Id == 2001);
        Assert.Equal((HrotWeaponPosture.Aimed, 2002, new HrotVec3(40, 10, 1)), (first.Weapon, first.AimTargetId, first.AimPoint));   // the chest stays as the fallback

        // the target walks 20 m: the shooter's entity state follows the chest (the entity sides - DIS, HLA and other gateways - publish the
        // aim point and re-send it on a move); DPS takes the target id from it and does not stream the point (PersonAim, DD-020 3.2)
        h.Move(target, new Vector3(60, 10, 0));
        var moved = Assert.Single(Gather(h, 1.1).Of<HrotEntityState>(), s => s.Id == 2001);
        Assert.Equal((2002, new HrotVec3(60, 10, 1)), (moved.AimTargetId, moved.AimPoint));
        Assert.DoesNotContain(Gather(h, 1.2).Of<HrotEntityState>(), s => s.Id == 2001);   // nothing moved: nothing re-sent
    }

    [Fact]
    public void A_point_aim_has_no_target_and_a_moved_point_is_a_new_state()
    {
        using var h = Live();
        var shooter = Soldier(h, 2001, Vector3.Zero);
        h.World.SetComponent(shooter, new WeaponPosture { Kind = WeaponPostureKind.Aimed, Point = new WorldPoint(30, 5, 1), HeldItem = HeldItemKind.Weapon });

        var first = Assert.Single(Gather(h, 1.0).Of<HrotEntityState>(), s => s.Id == 2001);
        Assert.Equal((0, new HrotVec3(30, 5, 1)), (first.AimTargetId, first.AimPoint));
        Assert.DoesNotContain(Gather(h, 1.1).Of<HrotEntityState>(), s => s.Id == 2001);

        h.World.SetComponent(shooter, new WeaponPosture { Kind = WeaponPostureKind.Aimed, Point = new WorldPoint(35, 5, 1), HeldItem = HeldItemKind.Weapon });
        var moved = Assert.Single(Gather(h, 1.2).Of<HrotEntityState>(), s => s.Id == 2001);
        Assert.Equal(new HrotVec3(35, 5, 1), moved.AimPoint);
    }

    [Fact]
    public void A_look_names_its_entity_and_only_the_head_going_on_or_off_is_a_new_detail()
    {
        using var h = Live();
        var target = h.AddHrotEntity(2002, new Vector3(40, 10, 0), tkb: 300);
        var man = Soldier(h, 2001, Vector3.Zero);
        h.World.AddComponent(man, new LookState { Target = new Vector3(40, 10, 0), TargetEntity = target, HasTarget = 1, HeadYaw = new Angle(0.5f) });

        var first = Assert.Single(Gather(h, 1.0).Of<HrotPersonDetail>(), d => d.Id == 2001);
        Assert.Equal((true, 2002, 0.5f), (first.HasLookTarget, first.LookTargetId, first.HeadYaw));

        // the target walks, the head keeps turning: the same intent
        h.World.SetComponent(man, new LookState { Target = new Vector3(60, 10, 0), TargetEntity = target, HasTarget = 1, HeadYaw = new Angle(0.8f) });
        Assert.DoesNotContain(Gather(h, 1.1).Of<HrotPersonDetail>(), d => d.Id == 2001);

        // the head comes straight on the body: one detail
        h.World.SetComponent(man, new LookState { Target = new Vector3(60, 10, 0), TargetEntity = target, HasTarget = 1, HeadYaw = new Angle(0f) });
        var straight = Assert.Single(Gather(h, 1.2).Of<HrotPersonDetail>(), d => d.Id == 2001);
        Assert.Equal((2002, 0f), (straight.LookTargetId, straight.HeadYaw));

        // a point look (no entity) keeps its point
        h.World.SetComponent(man, new LookState { Target = new Vector3(5, 5, 0), HasTarget = 1, HeadYaw = new Angle(0.4f) });
        var point = Assert.Single(Gather(h, 1.3).Of<HrotPersonDetail>(), d => d.Id == 2001);
        Assert.Equal((0, new HrotVec3(5, 5, 0)), (point.LookTargetId, point.LookTarget));
    }

    [Fact]
    public void A_stance_change_sends_its_start_and_blend_seconds_once_and_the_transition_progress_nothing()
    {
        using var h = Live();
        var man = Soldier(h, 2001, Vector3.Zero);
        h.World.AddComponent(man, new StanceStatus { CurrentStance = StanceId.Standing, Phase = StanceTransitionPhase.Idle });
        var settled = Assert.Single(Gather(h, 10.0).Of<HrotPersonDetail>(), d => d.Id == 2001);
        Assert.Equal((0.0, 1f), (settled.StanceChangedAt, settled.StanceProgress));

        h.World.SetComponent(man, new StanceIntent { TargetStance = StanceId.Prone, BlendTime = 0.9f, Version = 1 });
        h.World.SetComponent(man, new StanceStatus
        {
            CurrentStance = StanceId.Standing, Phase = StanceTransitionPhase.Transitioning, TransitionProgress = 0f, FromStance = StanceId.Standing,
            TransitionStartedAt = new SimInstant(12.4), TransitionSeconds = new SimDuration(0.9f),
        });
        var change = Assert.Single(Gather(h, 12.45).Of<HrotPersonDetail>(), d => d.Id == 2001);
        Assert.Equal((HrotStance.Prone, 12.4, 0.9f, 0f), (change.TargetStance, change.StanceChangedAt, change.StanceSeconds, change.StanceProgress));

        // the animation runs: the progress is not streamed
        foreach (float progress in new[] { 0.2f, 0.5f, 0.8f })
        {
            h.World.SetComponent(man, new StanceStatus
            {
                CurrentStance = StanceId.Standing, Phase = StanceTransitionPhase.Transitioning, TransitionProgress = progress, FromStance = StanceId.Standing,
                TransitionStartedAt = new SimInstant(12.4), TransitionSeconds = new SimDuration(0.9f),
            });
            Assert.DoesNotContain(Gather(h, 12.4 + progress).Of<HrotPersonDetail>(), d => d.Id == 2001);
        }
    }

    /// <summary>
    /// T-071: HROT's brain reads <c>UseVerb</c> as an INDEX into the offered list, so a verb NAME resolves to its position there (this test used to pin name -> HROT's verb
    /// NUMBER, which the brain refused unless the verb happened to sit at that index). Invariants over seeded random offered lists: a named verb that is offered is found at
    /// the position of its first entry; one that is not (or a name HROT does not have) is NotOffered (past every list, so HROT answers NothingToDo); None keeps the raw index.
    /// </summary>
    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    public void A_use_verb_name_resolves_to_its_position_in_the_offered_list_and_the_number_is_a_raw_index(int seed)
    {
        PlayerControl Frame(byte verb, string name) => new(1, 0, 0, 0, 0, 0, 0, false, 0, default, 0, 0, 0, 0, 0, 1, verb, 0, 0, false, default, default, 0, name);
        var kinds = Enum.GetValues<Fdp.Toolkit.Behavior.Possession.UseVerbKind>().Where(k => k != Fdp.Toolkit.Behavior.Possession.UseVerbKind.NoVerb).ToArray();
        var rng = new Random(seed);
        for (int round = 0; round < 200; round++)
        {
            int n = rng.Next(0, 9);
            var list = new Fdp.Toolkit.Behavior.Possession.UseAction[n];
            for (int i = 0; i < n; i++)
            {
                list[i] = new Fdp.Toolkit.Behavior.Possession.UseAction(kinds[rng.Next(kinds.Length)], "x", 0, true, Fdp.Toolkit.Behavior.Possession.UseReason.Ok, default, 0f);
            }

            foreach (var kind in kinds)
            {
                byte got = HrotPlayerStation.UseVerbIndexOf(Frame((byte)rng.Next(0, 8), kind.ToString()), list);
                int first = Array.FindIndex(list, a => a.Verb == kind);
                Assert.Equal(first < 0 ? HrotPlayerStation.NotOffered : (byte)first, got);
                if (first >= 0)
                {
                    Assert.Equal(kind, list[got].Verb);   // the position names the verb that was asked
                }
            }

            byte raw = (byte)rng.Next(0, 8);
            Assert.Equal(raw, HrotPlayerStation.UseVerbIndexOf(Frame(raw, string.Empty), list));   // None: the old byte, a raw index (0 = the primary)
            Assert.Equal(HrotPlayerStation.NotOffered, HrotPlayerStation.UseVerbIndexOf(Frame(raw, "Levitate"), list));   // a verb HROT does not have is not another verb
            Assert.Equal(HrotPlayerStation.NotOffered, HrotPlayerStation.UseVerbIndexOf(Frame(raw, "5"), list));   // a number is not a name
        }
    }

    [Fact]
    public void The_staged_fight_sends_a_fraction_of_the_person_samples()
    {
        var (person, aim, pose) = StagedFight("measure-fight");
        output.WriteLine($"AFTER: PersonState samples: {person}, PersonAim samples: {aim}, EntityState samples: {pose} ({Men} men aiming at {Men} moving targets for 10 s)");

        // before DD-020 the same fight wrote 1380 PersonState samples (measured with this very staging on the previous code): the aim, look and head streamed them
        Assert.True(person <= 1380 / 10, $"{person} PersonState samples");
        Assert.True(aim <= Men * 6, $"{aim} PersonAim samples");   // one per man, plus the heartbeat
    }
}
