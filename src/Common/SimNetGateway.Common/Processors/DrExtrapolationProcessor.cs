using SimNetGateway.Engine;

namespace SimNetGateway.Common;

/// <summary>
/// The attributes a dead-reckoning processor reads. Every id comes from the gateway's schema (Common knows no
/// vocabulary). Velocity, Acceleration, Orientation and AngularVelocity are optional: pass <see cref="AttrId.None"/>
/// when the schema has none, or the entity lacks the value, and zero is used.
/// </summary>
/// <param name="Position">Vec3, ECEF metres; required.</param>
/// <param name="Velocity">Vec3, optional.</param>
/// <param name="Acceleration">Vec3, optional.</param>
/// <param name="Orientation">Vec3, geocentric Euler psi/theta/phi, optional.</param>
/// <param name="AngularVelocity">Vec3, body axes rad/s, optional.</param>
/// <param name="Algorithm">Int, the strategy id (DIS numbering for the DIS predictors); required.</param>
/// <param name="StateTime">Double, simulation time at which the values were valid; required.</param>
public sealed record DrAttributeSet(
    AttrId Position,
    AttrId Velocity,
    AttrId Acceleration,
    AttrId Orientation,
    AttrId AngularVelocity,
    AttrId Algorithm,
    AttrId StateTime);

/// <summary>
/// Dead-reckoning extrapolation as a processor (DD-001 section 8, R-024, R-029): every <c>period</c> it predicts the
/// selected entities to the current simulation time and writes the result as derived attributes in one transaction,
/// only for entities whose predicted position changed. The input attributes are never written (R-020). A static
/// entity, or a paused session (frozen simulation time), therefore produces no writes and no triggers.
/// <para>
/// Entities are selected by having Position, Algorithm and StateTime, optionally known to an identity map and optionally
/// by a caller predicate. An entity whose algorithm has no registered predictor is skipped; a warning is recorded once
/// per strategy id. The rule is named "Extrapolate"; disabling it switches the module off.
/// </para>
/// </summary>
public sealed class DrExtrapolationProcessor : IGatewayModule
{
    private readonly SourceId _source;
    private readonly TimeSpan _period;
    private readonly DrAttributeSet _input;
    private readonly AttrId _predictedPosition;
    private readonly AttrId _predictedAt;
    private readonly PredictorRegistry _predictors;
    private readonly IIdentityMap? _knownTo;
    private readonly Func<EntityRecord, bool>? _select;
    private readonly List<EntityRecord> _entities = new();
    private readonly HashSet<int> _warnedStrategies = new();
    private IGatewayContext _gateway = null!;

    public DrExtrapolationProcessor(
        string name,
        SourceId source,
        TimeSpan period,
        DrAttributeSet input,
        AttrId predictedPosition,
        AttrId predictedAt,
        PredictorRegistry predictors,
        IIdentityMap? knownTo = null,
        Func<EntityRecord, bool>? select = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(name);
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(predictors);
        if (period <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(period), "The period must be positive.");
        }

        if (input.Position.IsNone || input.Algorithm.IsNone || input.StateTime.IsNone)
        {
            throw new ArgumentException("Position, Algorithm and StateTime are required.", nameof(input));
        }

        if (predictedPosition.IsNone || predictedAt.IsNone)
        {
            throw new ArgumentException("The derived attributes are required.");
        }

        Name = name;
        _source = source;
        _period = period;
        _input = input;
        _predictedPosition = predictedPosition;
        _predictedAt = predictedAt;
        _predictors = predictors;
        _knownTo = knownTo;
        _select = select;
    }

    public string Name { get; }

    public void Register(ModuleSetup setup)
    {
        _gateway = setup.Gateway;
        setup.Produces(_predictedPosition, _predictedAt);

        var consumed = new List<AttrId> { _input.Position, _input.Algorithm, _input.StateTime };
        foreach (var optional in new[] { _input.Velocity, _input.Acceleration, _input.Orientation, _input.AngularVelocity })
        {
            if (!optional.IsNone)
            {
                consumed.Add(optional);
            }
        }

        setup.Consumes(consumed.ToArray());
        setup.Every(_period).Named("Extrapolate").Do(Extrapolate);
    }

    private void Extrapolate(TimerTrigger trigger)
    {
        double now = _gateway.SimTime;
        var query = _gateway.State.Query().Has(_input.Position).Has(_input.Algorithm).Has(_input.StateTime);
        if (_knownTo is not null)
        {
            query = query.KnownTo(_knownTo);
        }

        if (_select is not null)
        {
            query.Where(_select).CopyTo(_entities);
        }
        else
        {
            query.CopyTo(_entities);
        }

        ChangeSet? changes = null;
        try
        {
            for (int i = 0; i < _entities.Count; i++)
            {
                var entity = _entities[i];
                int strategy = (int)entity.Get(_input.Algorithm).AsInt();
                if (!_predictors.TryGet(strategy, out var predictor))
                {
                    WarnOnce(strategy);
                    continue;
                }

                var basis = new PredictionBasis(
                    entity.Get(_input.Position).AsVec3(),
                    ReadVec(entity, _input.Velocity),
                    ReadVec(entity, _input.Acceleration),
                    ReadVec(entity, _input.Orientation),
                    ReadVec(entity, _input.AngularVelocity),
                    entity.Get(_input.StateTime).AsDouble());
                var predicted = predictor.Predict(in basis, now);

                if (entity.TryGet(_predictedPosition, out var stored) && stored.AsVec3() == predicted.Position)
                {
                    continue;
                }

                changes ??= _gateway.BeginChange(_source);
                changes.Set(entity.Id, _predictedPosition, AttrValue.From(predicted.Position));
                changes.Set(entity.Id, _predictedAt, AttrValue.From(now));
            }

            changes?.Commit();
        }
        finally
        {
            changes?.Discard(); // a no-op once committed; closes the transaction if something threw
            _entities.Clear();
        }
    }

    private static Vec3 ReadVec(EntityRecord entity, AttrId attr) =>
        !attr.IsNone && entity.TryGet(attr, out var value) ? value.AsVec3() : Vec3.Zero;

    private void WarnOnce(int strategy)
    {
        if (_warnedStrategies.Add(strategy))
        {
            _gateway.Diagnostics.Warn(Name, $"No predictor registered for dead-reckoning strategy {strategy}; entities using it are skipped.");
        }
    }
}
