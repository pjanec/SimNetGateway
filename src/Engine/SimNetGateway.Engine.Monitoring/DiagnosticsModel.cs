namespace SimNetGateway.Engine.Monitoring;

/// <summary>A copy of the diagnostics counters.</summary>
public readonly record struct DiagnosticsCounters(
    long MessagesProcessed,
    long UnhandledMessages,
    long TransactionFailures,
    long RuleFailures,
    long CascadeOverflows,
    long SessionRejections,
    long EventsWithoutSubscriber,
    int EntriesRetained,
    int EntryCapacity);

/// <summary>
/// The Diagnostics panel's model (DD-005 section 3): the counters and the recent entries, filtered by minimum severity and by
/// text (category, origin or message, case-insensitive). Entries are plain values (<see cref="DiagnosticEntry"/>).
/// </summary>
public sealed class DiagnosticsModel
{
    private readonly GatewayEngine _engine;
    private readonly List<DiagnosticEntry> _entries = new();

    public DiagnosticsModel(GatewayEngine engine)
    {
        ArgumentNullException.ThrowIfNull(engine);
        _engine = engine;
    }

    public DiagnosticSeverity MinSeverity { get; set; } = DiagnosticSeverity.Info;

    public string? Text { get; set; }

    public DiagnosticsCounters Counters { get; private set; }

    /// <summary>The entries that pass the filter, oldest first.</summary>
    public IReadOnlyList<DiagnosticEntry> Entries => _entries;

    public void Refresh()
    {
        var d = _engine.Diagnostics;
        Counters = new DiagnosticsCounters(d.MessagesProcessed, d.UnhandledMessages, d.TransactionFailures, d.RuleFailures,
            d.CascadeOverflows, d.SessionRejections, d.EventsWithoutSubscriber, d.Count, d.Capacity);
        string? text = string.IsNullOrEmpty(Text) ? null : Text;
        _entries.Clear();
        foreach (var entry in d.Recent)
        {
            if (entry.Severity < MinSeverity)
            {
                continue;
            }

            if (text is not null
                && entry.Category.IndexOf(text, StringComparison.OrdinalIgnoreCase) < 0
                && entry.Origin.IndexOf(text, StringComparison.OrdinalIgnoreCase) < 0
                && entry.Message.IndexOf(text, StringComparison.OrdinalIgnoreCase) < 0)
            {
                continue;
            }

            _entries.Add(entry);
        }
    }
}
