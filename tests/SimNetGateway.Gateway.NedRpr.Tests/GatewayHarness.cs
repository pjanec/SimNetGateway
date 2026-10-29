using SimNetGateway.Engine;
using SimNetGateway.Engine.InMemory;
using SimNetGateway.Protocol.Display;
using SimNetGateway.Protocol.Ned;
using SimNetGateway.Protocol.Rpr;
using SimNetGateway.Showcase;

namespace SimNetGateway.Gateway.NedRpr.Tests;

/// <summary>
/// The reusable end-to-end harness of the NED-RPR gateway: a manual clock, the gateway built from a configuration, one
/// multi-type in-memory inbound channel per input side, recorders for the three outbound sides, the RPR side wired to the HLA
/// transport on the fake RTI (<see cref="NedRprHost"/>: registrations are answered by the RTI one tick later, handles are the RTI's), and the <see cref="InvariantChecker"/> of the showcase project that runs after every step (G-1 .. G-8 as far as
/// the harness has the data: no probe is registered, so the probe parts of G-6 and G-8 are left to the showcase runner).
/// </summary>
public sealed class GatewayHarness
{
    /// <summary>One engine tick: 60 Hz.</summary>
    public const double Dt = NedRprHost.Dt;

    public GatewayHarness(
        GatewayConfiguration? configuration = null,
        Action<GatewayBuilder>? beforeBuild = null,
        Action<GatewayBuilder>? beforeBind = null,
        bool installChecker = true)
    {
        Clock = new ManualClock();
        var options = new GatewayOptions { JournalCapacity = 16384, DiagnosticsCapacity = 8192, EventLogCapacity = 4096 };
        Host = new NedRprHost(configuration ?? NedRprConfigurations.Default, Clock, new NedRprHostOptions { EngineOptions = options }, beforeBuild);
        NedOut = Host.NedOut!;
        RprOut = Host.RprOut!;
        DisplayOut = Host.DisplayOut!;
        Checker = new InvariantChecker(Host);
        if (installChecker)
        {
            NedOut.Tap = message =>
            {
                Checker.OnNedSent(message);
                if (LoopbackNed)
                {
                    EnqueueNed(message);
                }
            };
            RprOut.Tap = message =>
            {
                Checker.OnRprSent(message);
                if (LoopbackRpr && message is RprObjectUpdate or RprObjectRemoved or RprWeaponFire or RprMunitionDetonation)
                {
                    EnqueueRpr(message);
                }
            };
        }
    }

    /// <summary>The shared host assembly the harness wraps.</summary>
    public NedRprHost Host { get; }

    public ManualClock Clock { get; }

    public GatewayBuilder Builder => Host.Builder;

    public GatewayHostBinding Binding => Host.Binding;

    public GatewayEngine Engine => Host.Engine;

    public PivotSchema Schema => Host.Schema;

    public InMemoryInbound NedIn => Host.NedIn;

    public InMemoryInbound RprIn => Host.RprIn;

    public InMemoryOutbound<INedMessage> NedOut { get; }

    public InMemoryOutbound<IRprMessage> RprOut { get; }

    public InMemoryOutbound<IDisplayMessage> DisplayOut { get; }

    /// <summary>The handles the RTI answered the gateway's registrations with, in answer order.</summary>
    public IReadOnlyList<RprObjectHandle> RegisteredHandles => Host.RegisteredHandles;

    public InvariantChecker Checker { get; }

    public SourceId NedSource => Host.NedSource;

    public SourceId RprSource => Host.RprSource;

    public IdentityMap<ulong> NedIds => Host.NedIds;

    public IdentityMap<RprObjectHandle> RprHandles => Host.RprHandles;

    public IdentityMap<DisEntityId> RprEntityIds => Host.RprEntityIds;

    public IdentityMap<string> DisplayLabels => Host.DisplayLabels;

    /// <summary>When false the transport is not pumped: the RTI's answer to a registration is not delivered (the handle stays pending).</summary>
    public bool DeliverRti
    {
        get => Host.DeliverRti;
        set => Host.DeliverRti = value;
    }

    /// <summary>NED outbound messages are fed back into the NED inbound channel (a DDS loopback).</summary>
    public bool LoopbackNed { get; set; }

    /// <summary>RPR outbound updates, removals and events are fed back into the RPR inbound channel.</summary>
    public bool LoopbackRpr { get; set; }

    public double SimTime => Engine.SimTime;

    public double WallTime => Clock.Now;

    // ---- stepping ---------------------------------------------------------------------------------------------------

    /// <summary>Advances the clock by one tick, lets the RTI answer, ticks the engine, then checks the invariants.</summary>
    public void Step()
    {
        Host.Step(Dt);
        Checker.Check();
    }

    public void Steps(int count)
    {
        for (int i = 0; i < count; i++)
        {
            Step();
        }
    }

    /// <summary>Runs the given number of simulated seconds of ticks.</summary>
    public void Run(double seconds) => Steps((int)Math.Round(seconds / Dt));

    /// <summary>Steps until the condition holds (checked after each step); fails after <paramref name="maxSeconds"/>.</summary>
    public void RunUntil(Func<bool> condition, double maxSeconds = 30, string? what = null)
    {
        int max = (int)Math.Round(maxSeconds / Dt);
        for (int i = 0; i < max; i++)
        {
            if (condition())
            {
                return;
            }

            Step();
        }

        Assert.True(condition(), what ?? "the condition did not hold in time");
    }

    // ---- injection --------------------------------------------------------------------------------------------------

    public void Ned<T>(T message)
        where T : INedMessage
    {
        Checker.OnNedReceived(message);
        NedIn.Enqueue(in message);
    }

    public void Rpr<T>(T message)
        where T : IRprMessage
    {
        Checker.OnRprReceived(message);
        RprIn.Enqueue(in message);
    }

    /// <summary>Injects NED messages and runs one step.</summary>
    public void NedStep<T>(T message)
        where T : INedMessage
    {
        Ned(message);
        Step();
    }

    public void RprStep<T>(T message)
        where T : IRprMessage
    {
        Rpr(message);
        Step();
    }

    private void EnqueueNed(INedMessage message)
    {
        switch (message)
        {
            case NedEntityMaster m: NedIn.Enqueue(in m); break;
            case NedGeoSpatial m: NedIn.Enqueue(in m); break;
            case NedEntityDamage m: NedIn.Enqueue(in m); break;
            case NedWeaponStatus m: NedIn.Enqueue(in m); break;
            case NedEntityDisposed m: NedIn.Enqueue(in m); break;
            case NedWeaponFire m: NedIn.Enqueue(in m); break;
            case NedDetonation m: NedIn.Enqueue(in m); break;
            case NedSimControl m: NedIn.Enqueue(in m); break;
            case NedFormation m: NedIn.Enqueue(in m); break;
            case NedFireAmmunition m: NedIn.Enqueue(in m); break;
            case NedDamageNotification m: NedIn.Enqueue(in m); break;
            case NedIffStatus m: NedIn.Enqueue(in m); break;
            default: throw new InvalidOperationException("Unknown NED message " + message.GetType().Name);
        }
    }

    private void EnqueueRpr(IRprMessage message)
    {
        switch (message)
        {
            case RprObjectUpdate m: RprIn.Enqueue(in m); break;
            case RprObjectRemoved m: RprIn.Enqueue(in m); break;
            case RprWeaponFire m: RprIn.Enqueue(in m); break;
            case RprMunitionDetonation m: RprIn.Enqueue(in m); break;
            case RprAggregateEntity m: RprIn.Enqueue(in m); break;
            default: throw new InvalidOperationException("Cannot loop back " + message.GetType().Name);
        }
    }

    // ---- inspection ---------------------------------------------------------------------------------------------------

    /// <summary>Clears the three outbound recorders (the checker keeps its own record of what it has seen).</summary>
    public void ClearOutput()
    {
        NedOut.Clear();
        RprOut.Clear();
        DisplayOut.Clear();
    }

    public List<T> NedSent<T>()
        where T : INedMessage => NedOut.OfType<T>().ToList();

    public List<T> RprSent<T>()
        where T : IRprMessage => RprOut.OfType<T>().ToList();

    public List<T> DisplaySent<T>()
        where T : IDisplayMessage => DisplayOut.OfType<T>().ToList();

    public EntityRecord? EntityOfNed(ulong id) => NedIds.TryResolve(id, out var e) ? Engine.State.Find(e) : null;

    public EntityRecord? EntityOfHandle(RprObjectHandle handle) => RprHandles.TryResolve(handle, out var e) ? Engine.State.Find(e) : null;

    public RprObjectHandle HandleOf(EntityRecord entity) =>
        RprHandles.TryGetKey(entity.Id, out var handle) ? handle : throw new InvalidOperationException("The entity has no RPR handle.");

    public bool Has(EntityRecord e, AttrId attr) => e.Has(attr);

    /// <summary>The retained change set records, oldest first (valid while they are in the journal).</summary>
    public List<ChangeSetRecord> Records()
    {
        var list = new List<ChangeSetRecord>();
        foreach (var record in Engine.State.Journal.Since(double.NegativeInfinity))
        {
            list.Add(record);
        }

        return list;
    }

    public RuleInfo Rule(string fullName) => Engine.FindRule(fullName) ?? throw new KeyNotFoundException(fullName);

    /// <summary>
    /// A snapshot of every slot version of every live entity, without the derived attributes the DR processor keeps writing:
    /// equal snapshots mean nothing received or stored was written.
    /// </summary>
    public string Snapshot()
    {
        var parts = new List<string>();
        foreach (var entity in Engine.State.Entities)
        {
            var slots = new List<string>();
            foreach (var (attr, slot) in entity.Attributes)
            {
                if (attr == Schema.PredictedPosition || attr == Schema.PredictedAt)
                {
                    continue;
                }

                slots.Add($"{attr.Value}:{slot.Meta.Version}:{slot.Meta.Correlation.Value}");
            }

            slots.Sort(StringComparer.Ordinal);
            parts.Add($"{entity.Id.Value}[{string.Join(",", slots)}]");
        }

        parts.Sort(StringComparer.Ordinal);
        return string.Join(";", parts);
    }
}
