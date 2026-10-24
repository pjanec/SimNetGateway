namespace SimNetGateway.Engine;

/// <summary>A message handler: records commands, events and session commands in <paramref name="ctx"/>.</summary>
public delegate void IngressHandler<T>(in T message, IngressContext ctx);

/// <summary>
/// The sandbox of ingress handlers (one reused instance): the open change set, read access to committed state,
/// pending events and session commands - all bound to the current correlation id (DD-001 10.3).
/// </summary>
public sealed class IngressContext
{
    private readonly GatewayEngine _engine;

    internal IngressContext(GatewayEngine engine)
    {
        _engine = engine;
        Changes = null!;
        State = engine.State;
        Session = engine.Session;
        Diagnostics = engine.Diagnostics;
        CurrentModule = string.Empty;
    }

    public SourceId Source { get; private set; }
    public CorrelationId Correlation { get; private set; }
    public double ReceivedAt { get; private set; }
    public double WallTime { get; private set; }
    public double SimTime { get; private set; }
    public ChangeSet Changes { get; private set; }
    public IStateView State { get; }
    public ISessionView Session { get; }
    public Diagnostics Diagnostics { get; }

    public bool IsAborted { get; private set; }

    /// <summary>Why the transaction was aborted (the first reason wins); null when not aborted.</summary>
    public string? AbortReason { get; private set; }

    /// <summary>The module of the handler that is running (or that aborted, once aborted).</summary>
    public string CurrentModule { get; internal set; }

    internal string? AbortedBy { get; private set; }

    internal List<SimEvent> PendingEvents { get; } = new();
    internal List<SessionCommand> PendingCommands { get; } = new();
    internal List<AttributeBag> Rented { get; } = new();

    /// <summary>A payload from the pool; publish it or it is returned when the message finishes.</summary>
    public AttributeBag RentPayload()
    {
        var bag = _engine.State.Pools.Bags.Rent();
        Rented.Add(bag);
        return bag;
    }

    /// <summary>Queues an event; it is dispatched after the transaction's entity triggers, only if it commits.</summary>
    public void Publish(EventTypeId type, AttributeBag payload)
    {
        ArgumentNullException.ThrowIfNull(payload);
        if (payload.Published)
        {
            throw new InvalidOperationException("The payload was already published.");
        }

        payload.Published = true;
        var ev = _engine.State.Pools.Events.Rent();
        ev.Type = type;
        ev.Payload = payload;
        ev.Source = Source;
        ev.Correlation = Correlation;
        ev.CausedBy = default;
        ev.WallTime = WallTime;
        ev.SimTime = SimTime;
        PendingEvents.Add(ev);
    }

    /// <summary>Queues a session command, executed after the message's own state changes.</summary>
    public void Command(SessionCommand command)
    {
        ArgumentNullException.ThrowIfNull(command);
        PendingCommands.Add(command);
    }

    /// <summary>Discards the whole transaction (state, events, commands); later handlers of the message do not run.</summary>
    public void Abort(string reason)
    {
        if (!IsAborted)
        {
            IsAborted = true;
            AbortReason = reason;
            AbortedBy = CurrentModule;
        }
    }

    internal void Begin(ChangeSet changes, SourceId source, double receivedAt, double wallTime, double simTime)
    {
        Changes = changes;
        Source = source;
        Correlation = changes.Correlation;
        ReceivedAt = receivedAt;
        WallTime = wallTime;
        SimTime = simTime;
        IsAborted = false;
        AbortReason = null;
        AbortedBy = null;
        CurrentModule = string.Empty;
        PendingEvents.Clear();
        PendingCommands.Clear();
        Rented.Clear();
    }
}
