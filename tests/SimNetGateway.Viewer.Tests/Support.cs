using SimNetGateway.Engine;

namespace SimNetGateway.Viewer.Tests;

/// <summary>
/// A window launcher that must never be reached: headless runs and refused window runs must not initialise raylib (R-076).
/// A tester that wants to simulate a machine without a display passes <see cref="Problem"/>.
/// </summary>
internal sealed class RecordingLauncher : IWindowedLauncher
{
    public string? Problem { get; init; }

    public int Checks { get; private set; }

    public int Runs { get; private set; }

    public string? CheckEnvironment()
    {
        Checks++;
        return Problem;
    }

    public int Run(ViewerSession session, ViewerCommandLine line)
    {
        Runs++;
        return 0;
    }
}

/// <summary>A time source for the tick budget that only moves when the test says so (a tick "costs" a fixed number of ms).</summary>
internal sealed class FakeTimer
{
    public long Milliseconds { get; set; }

    public long Now() => Milliseconds;
}

/// <summary>An observer that does nothing; tests derive from it for the hooks they need.</summary>
internal class NullObserver : IDispatchObserver
{
    public virtual void InputStarted(CorrelationId correlation, SourceId source, Type messageType)
    {
    }

    public virtual void InputUnhandled(SourceId source, Type messageType)
    {
    }

    public virtual void InputFinished(CorrelationId correlation)
    {
    }

    public virtual void InputAborted(CorrelationId correlation, SourceId source, string module, string reason)
    {
    }

    public virtual void EventPublished(CorrelationId correlation, CorrelationId causedBy, SourceId source, EventTypeId type, int depth)
    {
    }

    public virtual void Committed(in CommitSummary summary)
    {
    }

    public virtual void CommitRejected(CorrelationId correlation, SourceId source, string reason)
    {
    }

    public virtual void TriggerRaised(DispatchTriggerKind kind, EntityId entity, EventTypeId eventType, CorrelationId correlation, int depth)
    {
    }

    public virtual void RuleEvaluated(RuleInfo rule, RuleOutcome outcome, CorrelationId correlation)
    {
    }

    public virtual void SessionCommandApplied(SessionCommand command, SourceId source, CorrelationId correlation)
    {
    }

    public virtual void SessionCommandRejected(SessionCommand command, SourceId source, CorrelationId correlation, string reason)
    {
    }
}

internal static class Drive
{
    /// <summary>The first live entity of the engine (in repository order) that matches; the repository's enumerable has no LINQ.</summary>
    public static EntityId FirstEntity(ViewerSession session, Func<EntityRecord, bool>? match = null)
    {
        foreach (var entity in session.Engine.State.Entities)
        {
            if (match is null || match(entity))
            {
                return entity.Id;
            }
        }

        throw new InvalidOperationException("no such entity");
    }

    /// <summary>An entity of the NED side that moves fast enough to be written to every few ticks (traffic generator).</summary>
    public static EntityId MovingEntity(ViewerSession session) => FirstEntity(session, e =>
        e.Origin == session.Host.NedSource && e.TryGet(session.Schema.Velocity, out var v)
        && Math.Sqrt(v.AsVec3().X * v.AsVec3().X + v.AsVec3().Y * v.AsVec3().Y + v.AsVec3().Z * v.AsVec3().Z) > 20);

    /// <summary>Runs <paramref name="frames"/> frames of <paramref name="seconds"/> each.</summary>
    public static void Frames(ViewerSession session, int frames, double seconds = 1.0 / 60.0)
    {
        for (int i = 0; i < frames; i++)
        {
            session.Frame(seconds);
        }
    }

    /// <summary>A session that never limits ticks by wall time (so that results do not depend on the machine).</summary>
    public static ViewerSessionOptions Unlimited(ViewerScenario scenario, double speed = 1.0, bool playing = true, int entities = 60) => new()
    {
        Scenario = scenario,
        Speed = speed,
        Playing = playing,
        Entities = entities,
        TickBudgetMs = double.PositiveInfinity,
    };
}
