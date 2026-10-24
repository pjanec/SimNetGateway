using SimNetGateway.Engine;

namespace SimNetGateway.Common;

/// <summary>
/// Destroys the entities of one side that the side itself did not write within a timeout (DD-001 6.5, R-068): the
/// receiving mirror of a protocol heartbeat. Every <c>checkPeriod</c> the rule "Reap" queries
/// <c>FromOrigin(side).NotOriginWrittenWithin(timeout)</c> and destroys the matches in one transaction under
/// <c>reaperSource</c>; the destruction is an ordinary removal for the other sides. Only origin writes (by the
/// entity's owner) count, so derived writes of processors (e.g. dead-reckoning predictions) do not keep an entity
/// alive. Entities of other sides are never touched. Timing follows <c>NotOriginWrittenWithin</c>: an entity exactly
/// <c>timeout</c> old is still alive.
/// </summary>
public sealed class StaleEntityReaper : IGatewayModule
{
    private readonly SourceId _side;
    private readonly TimeSpan _timeout;
    private readonly TimeSpan _checkPeriod;
    private readonly SourceId _reaperSource;
    private readonly List<EntityRecord> _stale = new();
    private IGatewayContext _gateway = null!;

    public StaleEntityReaper(string name, SourceId side, TimeSpan timeout, TimeSpan checkPeriod, SourceId reaperSource)
    {
        ArgumentException.ThrowIfNullOrEmpty(name);
        if (timeout <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(timeout), "The timeout must be positive.");
        }

        if (checkPeriod <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(checkPeriod), "The check period must be positive.");
        }

        Name = name;
        _side = side;
        _timeout = timeout;
        _checkPeriod = checkPeriod;
        _reaperSource = reaperSource;
    }

    public string Name { get; }

    public void Register(ModuleSetup setup)
    {
        _gateway = setup.Gateway;
        setup.Every(_checkPeriod).Named("Reap").Do(Reap);
    }

    private void Reap(TimerTrigger trigger)
    {
        _gateway.State.Query().FromOrigin(_side).NotOriginWrittenWithin(_timeout).CopyTo(_stale);
        if (_stale.Count == 0)
        {
            return;
        }

        int count = _stale.Count;
        var changes = _gateway.BeginChange(_reaperSource);
        try
        {
            for (int i = 0; i < count; i++)
            {
                changes.DestroyEntity(_stale[i].Id);
            }

            changes.Commit();
        }
        finally
        {
            changes.Discard();
            _stale.Clear();
        }

        _gateway.Diagnostics.Info(Name, $"Reaped {count} stale entities.");
    }
}
