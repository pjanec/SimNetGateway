namespace SimNetGateway.Engine;

public enum EntityTriggerKind
{
    Created,
    Updated,
    Removed,
}

/// <summary>One trigger per entity per change set (R-015). Reused: valid only during the rule call.</summary>
public sealed class EntityTrigger
{
    internal EntityTrigger()
    {
        Entity = null!;
        Delta = null!;
        ChangeSet = null!;
    }

    public EntityTriggerKind Kind { get; internal set; }
    public EntityRecord Entity { get; internal set; }
    public EntityId Id { get; internal set; }
    public EntityDelta Delta { get; internal set; }
    public ChangeSetRecord ChangeSet { get; internal set; }
    public SourceId Source { get; internal set; }
    public CorrelationId Correlation { get; internal set; }

    /// <summary>The attribute changed its value in this change set.</summary>
    public bool Changed(AttrId attr) => Delta.WasChanged(attr);

    /// <summary>The attribute was written in this change set, even with the same value.</summary>
    public bool Written(AttrId attr) => Delta.WasWritten(attr);
}

/// <summary>A simulation event reached the dispatcher. Reused: valid only during the rule call.</summary>
public sealed class EventTrigger
{
    internal EventTrigger()
    {
        Event = null!;
        Payload = null!;
    }

    public SimEvent Event { get; internal set; }
    public EventTypeId Type { get; internal set; }
    public AttributeBag Payload { get; internal set; }
    public SourceId Source { get; internal set; }
    public CorrelationId Correlation { get; internal set; }
}

/// <summary>An accepted session command. Reused: valid only during the rule call.</summary>
public sealed class SessionTrigger
{
    internal SessionTrigger()
    {
        Command = null!;
    }

    public SessionCommand Command { get; internal set; }
    public SessionState Previous { get; internal set; }
    public SessionState Current { get; internal set; }
    public SourceId Source { get; internal set; }
}

/// <summary>After all entity triggers of one committed change set. Reused.</summary>
public sealed class ChangeSetTrigger
{
    internal ChangeSetTrigger()
    {
        ChangeSet = null!;
    }

    public ChangeSetRecord ChangeSet { get; internal set; }
}

/// <summary>A timer period elapsed (<see cref="Period"/> is zero for tick-end rules). Reused.</summary>
public sealed class TimerTrigger
{
    internal TimerTrigger()
    {
    }

    public double WallTime { get; internal set; }
    public double SimTime { get; internal set; }
    public TimeSpan Period { get; internal set; }
}

/// <summary>A session command is about to be applied; a rule may veto it (DD-004 4.2, R-059). Reused.</summary>
public sealed class SessionRequestTrigger
{
    internal SessionRequestTrigger()
    {
        Command = null!;
    }

    public SessionCommand Command { get; internal set; }
    public SourceId Source { get; internal set; }
    public bool IsVetoed { get; private set; }
    public string? VetoReason { get; private set; }

    /// <summary>Refuses the command; the first reason is kept.</summary>
    public void Veto(string reason)
    {
        if (!IsVetoed)
        {
            IsVetoed = true;
            VetoReason = reason;
        }
    }

    internal void Reset(SessionCommand command, SourceId source)
    {
        Command = command;
        Source = source;
        IsVetoed = false;
        VetoReason = null;
    }
}
