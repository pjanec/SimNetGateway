namespace SimNetGateway.Common;

/// <summary>Predictors keyed by strategy id (R-060, DD-004 P-37).</summary>
public sealed class PredictorRegistry
{
    private readonly Dictionary<int, IPredictor> _byId = new();
    private readonly List<IPredictor> _all = new();

    /// <summary>Every registered predictor, in registration order.</summary>
    public IReadOnlyList<IPredictor> All => _all;

    public int Count => _all.Count;

    /// <summary>Registers a predictor; a second one with the same strategy id is a programming error.</summary>
    public void Register(IPredictor predictor)
    {
        ArgumentNullException.ThrowIfNull(predictor);
        if (!_byId.TryAdd(predictor.StrategyId, predictor))
        {
            throw new InvalidOperationException(
                $"A predictor with strategy id {predictor.StrategyId} ('{_byId[predictor.StrategyId].Name}') is already registered.");
        }

        _all.Add(predictor);
    }

    public bool TryGet(int strategyId, out IPredictor predictor)
    {
        if (_byId.TryGetValue(strategyId, out var found))
        {
            predictor = found;
            return true;
        }

        predictor = null!;
        return false;
    }

    /// <summary>A registry holding the DIS algorithms Static, FPW, RPW, RVW, FVW, FPB, RPB, RVB and FVB.</summary>
    public static PredictorRegistry CreateDis()
    {
        var registry = new PredictorRegistry();
        foreach (var algorithm in DisPredictor.Supported)
        {
            registry.Register(new DisPredictor(algorithm));
        }

        return registry;
    }
}
