using SimNetGateway.Engine;

namespace SimNetGateway.Showcase;

/// <summary>
/// Test-only processor (DD-002 act 7, S-7.4): the rule <c>RogueProcessor/Loop</c> reacts to a change of its own attribute
/// <c>Rogue.Counter</c> by writing that attribute again with the next value - an infinite cascade that only the engine's
/// cascade depth limit stops. The rule is registered disabled; the script enables it for one tick and starts the cascade with
/// <see cref="Kick"/>. Registered by the harness, never by the gateway profile.
/// </summary>
public sealed class RogueProcessor : IGatewayModule
{
    public const string LoopRule = "RogueProcessor/Loop";

    private IGatewayContext _gateway = null!;
    private SourceId _source;

    public string Name => nameof(RogueProcessor);

    /// <summary>The attribute the rule watches and rewrites.</summary>
    public AttrId Counter { get; private set; }

    /// <summary>The source of the rogue change sets.</summary>
    public SourceId Source => _source;

    public void Register(ModuleSetup setup)
    {
        _gateway = setup.Gateway;
        Counter = setup.Attributes.Register("Rogue.Counter", AttrKind.Int, "Test-only: counter that the rogue processor keeps rewriting");
        _source = setup.Sources.Register("harness.rogue");
        setup.Produces(Counter);
        setup.OnAttributesChanged(Counter).Named("Loop").Disabled().Do(Loop);
    }

    /// <summary>Starts the cascade: sets the counter of the entity to 1 (the rule, when enabled, takes it from there).</summary>
    public void Kick(EntityId entity)
    {
        var changes = _gateway.BeginChange(_source);
        try
        {
            changes.Set(entity, Counter, AttrValue.From(1));
            changes.Commit();
        }
        finally
        {
            changes.Discard();
        }
    }

    private void Loop(EntityTrigger trigger)
    {
        var changes = _gateway.BeginChange(_source);
        try
        {
            changes.Set(trigger.Id, Counter, AttrValue.From(trigger.Entity.Get(Counter).AsInt() + 1));
            changes.Commit();
        }
        finally
        {
            changes.Discard();
        }
    }
}
