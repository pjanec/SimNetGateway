namespace SimNetGateway.Engine.Monitoring;

/// <summary>
/// The Vocabulary panel's model (DD-005 section 3): the result of <see cref="GatewayEngine.Validate"/> (errors and warnings, as
/// immutable records) and the schema dump with producers and consumers.
/// </summary>
public sealed class VocabularyModel
{
    private readonly GatewayEngine _engine;

    public VocabularyModel(GatewayEngine engine)
    {
        ArgumentNullException.ThrowIfNull(engine);
        _engine = engine;
    }

    public IReadOnlyList<ValidationIssue> Errors { get; private set; } = Array.Empty<ValidationIssue>();

    public IReadOnlyList<ValidationIssue> Warnings { get; private set; } = Array.Empty<ValidationIssue>();

    public bool HasErrors => Errors.Count > 0;

    /// <summary>The whole vocabulary as text.</summary>
    public string Dump { get; private set; } = string.Empty;

    public void Refresh()
    {
        var report = _engine.Validate();
        Errors = report.Errors.ToArray();
        Warnings = report.Warnings.ToArray();
        Dump = report.Dump();
    }
}
