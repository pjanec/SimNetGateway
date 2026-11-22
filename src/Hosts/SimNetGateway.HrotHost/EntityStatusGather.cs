using Fdp.Core;
using Fdp.Core.CommandHierarchy;
using Fdp.Interfaces;
using Fdp.Toolkit.Behavior;
using Fdp.Toolkit.Behavior.Components;
using Fdp.Toolkit.Combat;
using Fdp.Toolkit.Combat.Components;
using Fdp.Toolkit.Physics.Components;
using GizmoMap.Contracts.Symbology;
using Hrot.Common.Diagnostics.Gizmos;
using Hrot.Core.Tkb;
using SimNetGateway.Engine.InMemory;
using SimNetGateway.Protocol.Hrot;

namespace SimNetGateway.HrotHost;

/// <summary>
/// What a client draws an entity's card from (DD-025), read from HROT's own rules and never computed here: the unit's symbol (<c>UnitSymbolDeriver</c> for its type in its side's frame, as
/// <c>UnitSymbols.TryOf</c> does), the health pair (<c>Health</c>, no fraction), a unit's strength and ammunition share (<c>UnitCardFacts</c>) and a man's mind-state bar (<c>MindStateBar.Of</c>). Only for
/// the entities HROT would give a card (<c>Seeable.OnMap</c>); the card readers keep HROT's own conditions (a unit is an entity with <c>UnitEntity</c>, a mind-state bar is a living person's).
/// Sent when a value changes by more than <see cref="Step"/> (floats; ints and strings exactly), compared with what was last sent; an entity that stops having a card is withdrawn once. A steady
/// gather allocates nothing (the symbol string is made once per unit type and side). Main thread only.
/// <para>What the unit is doing (T-063): <c>Behaviour</c> is the name the node's <c>BehaviorRegistry</c> gives <c>BehaviorState.ActiveBehaviorHash</c> (a name the registry owns: no string is made per frame),
/// <c>Task</c> the name of the task slot of <c>BrainIntentReader.TaskOf</c> in the scenario's scope. <c>TaskOf</c> builds a slot object, so it is called only when the entity's active behaviour (hash or
/// origin) differs from the last gather's; a steady world reads the cached pair.</para>
/// </summary>
internal sealed class EntityStatusGather
{
    /// <summary>The smallest change of a float field that is sent again (health, ammunition share, mind-state fill and break mark).</summary>
    public const float Step = 0.01f;

    private readonly Dictionary<int, HrotEntityStatus> _sent = new();
    private readonly Dictionary<(long Type, ForceId Side), string> _symbols = new();
    private ITkbDatabase? _symbolsFor;
    private readonly Dictionary<int, BrainNames> _brain = new();
    private BehaviorRegistry? _brainFor;

    /// <summary>The longest <c>Behaviour</c> / <c>Task</c> sent (the topic's bound); a longer name is cut.</summary>
    public const int MaxName = 32;

    /// <summary>What the last gather read for an entity's brain: the key it was read for and the two names.</summary>
    private readonly record struct BrainNames(int Hash, BehaviorOrigin Origin, string Behaviour, string Task);

    public void Clear()
    {
        _sent.Clear();
        _symbols.Clear();
        _symbolsFor = null;
        _brain.Clear();
        _brainFor = null;
    }

    public void Forget(int id)
    {
        _sent.Remove(id);
        _brain.Remove(id);
    }

    /// <summary>Gathers the status of <paramref name="e"/> (network id <paramref name="id"/>, TKB type <paramref name="tkbType"/>) and enqueues it when it is new or changed; an entity that has no card now but had one is withdrawn.</summary>
    public void Gather(EntityRepository world, ITkbDatabase? tkb, BehaviorRegistry? behaviors, Entity e, int id, long tkbType, InMemoryInbound inbound)
    {
        if (!Seeable.OnMap(world, e))
        {
            _brain.Remove(id);
            if (_sent.Remove(id))
            {
                inbound.Enqueue(new HrotEntityStatusRemoved(id));
            }

            return;
        }

        var now = Read(world, tkb, behaviors, e, id, tkbType);
        if (_sent.TryGetValue(id, out var sent) && !Differs(in sent, in now))
        {
            return;
        }

        _sent[id] = now;
        inbound.Enqueue(in now);
    }

    private HrotEntityStatus Read(EntityRepository world, ITkbDatabase? tkb, BehaviorRegistry? behaviors, Entity e, int id, long tkbType)
    {
        bool unit = world.IsComponentTypeRegistered<UnitEntity>() && world.HasComponent<UnitEntity>(e);

        // the health pair as HROT's bar reads it: none when there is no pool or its maximum is not positive
        float current = 0f, max = 0f;
        if (world.HasComponent<Health>(e))
        {
            ref readonly var health = ref world.GetComponentRO<Health>(e);
            if (health.Max > 0f && float.IsFinite(health.Max) && float.IsFinite(health.Current))
            {
                current = health.Current;
                max = health.Max;
            }
        }

        // a unit's strength and ammunition, as its card reads them (a card shows a strength bar only for a known, positive authorized strength)
        string symbol = string.Empty;
        int present = 0, authorized = 0;
        float ammo = -1f;
        if (unit)
        {
            var (men, menAuthorized, _, known) = UnitCardFacts.Strength(world, e);
            if (known && menAuthorized > 0)
            {
                present = men;
                authorized = menAuthorized;
            }

            float share = UnitCardFacts.AmmoShare(world, e);
            ammo = float.IsFinite(share) ? share : -1f;
            symbol = SymbolOf(world, tkb, e, tkbType);
        }

        // a living person's mind-state bar (HROT's gizmo draws it for nobody else)
        float fill = 0f, mark = -1f;
        var band = HrotMindBand.None;
        if (PhysicsColliderReaders.IsPerson(world, e) && !CombatLife.IsDestroyed(world, e))
        {
            var (f, tick, b) = MindStateBar.Of(world, e);
            if (b != MindBand.None && float.IsFinite(f))
            {
                fill = f;
                mark = float.IsFinite(tick) ? tick : -1f;
                band = b switch
                {
                    MindBand.Calm => HrotMindBand.Calm,
                    MindBand.Nervous => HrotMindBand.Nervous,
                    MindBand.Broken => HrotMindBand.Broken,
                    MindBand.Fear => HrotMindBand.Fear,
                    _ => HrotMindBand.None,
                };
            }
        }

        var (behaviour, task) = BrainOf(world, behaviors, e, id);
        return new HrotEntityStatus(id, symbol, current, max, present, authorized, ammo, fill, mark, band, behaviour, task);
    }

    /// <summary>
    /// The names of the behaviour the entity runs and of its task slot ("" = none, no registry or not known). Read again only when the active behaviour hash or its origin differ from the last
    /// gather's, because <c>BrainIntentReader.TaskOf</c> allocates its slot.
    /// </summary>
    private (string Behaviour, string Task) BrainOf(EntityRepository world, BehaviorRegistry? registry, Entity e, int id)
    {
        if (registry is null || !world.IsComponentTypeRegistered<BehaviorState>() || !world.HasComponent<BehaviorState>(e))
        {
            _brain.Remove(id);
            return (string.Empty, string.Empty);
        }

        if (!ReferenceEquals(_brainFor, registry))
        {
            _brain.Clear();   // another registry (a reload): the names are read again
            _brainFor = registry;
        }

        ref readonly var state = ref world.GetComponentRO<BehaviorState>(e);
        int hash = state.ActiveBehaviorHash;
        var origin = state.Origin;
        if (_brain.TryGetValue(id, out var known) && known.Hash == hash && known.Origin == origin)
        {
            return (known.Behaviour, known.Task);
        }

        string behaviour = hash != BehaviorIds.None && registry.TryGetName(hash, out string? name) ? Cut(name) : string.Empty;
        string task = BrainIntentReader.TaskOf(world, e, registry, BrainIntentScope.Ordered)?.Name ?? string.Empty;
        task = Cut(task);
        _brain[id] = new BrainNames(hash, origin, behaviour, task);
        return (behaviour, task);
    }

    private static string Cut(string name) => name.Length > MaxName ? name[..MaxName] : name;

    /// <summary>The unit's symbol in wire form: its type's derived symbol in its side's frame; "" when its type is not a unit's. Made once per type and side.</summary>
    private string SymbolOf(EntityRepository world, ITkbDatabase? tkb, Entity e, long tkbType)
    {
        if (tkb is null)
        {
            return string.Empty;
        }

        if (!ReferenceEquals(_symbolsFor, tkb))
        {
            _symbols.Clear();   // another template database (a reload): the derived symbols are read again
            _symbolsFor = tkb;
        }

        var side = world.HasComponent<EntityInfo>(e) ? world.GetComponentRO<EntityInfo>(e).ForceId : ForceId.Neutral;
        if (_symbols.TryGetValue((tkbType, side), out string? known))
        {
            return known;
        }

        string text = string.Empty;
        if (UnitSymbolDeriver.TryOfType(tkb, tkbType, out var derived))
        {
            Span<char> chars = stackalloc char[SymbolCode.MaxLength];
            var withSide = UnitSymbolDeriver.WithSide(derived, side);
            text = new string(chars[..withSide.Format(chars)]);
        }

        _symbols[(tkbType, side)] = text;
        return text;
    }

    private static bool Differs(in HrotEntityStatus a, in HrotEntityStatus b) =>
        a.StrengthPresent != b.StrengthPresent || a.StrengthAuthorized != b.StrengthAuthorized || a.MindBand != b.MindBand || !string.Equals(a.Symbol, b.Symbol, StringComparison.Ordinal)
        || !string.Equals(a.Behaviour, b.Behaviour, StringComparison.Ordinal) || !string.Equals(a.Task, b.Task, StringComparison.Ordinal)
        || Moved(a.HealthCurrent, b.HealthCurrent) || Moved(a.HealthMax, b.HealthMax) || Moved(a.AmmoShare, b.AmmoShare) || Moved(a.MindFill, b.MindFill) || Moved(a.MindBreak, b.MindBreak);

    private static bool Moved(float last, float now) => MathF.Abs(now - last) >= Step;
}
