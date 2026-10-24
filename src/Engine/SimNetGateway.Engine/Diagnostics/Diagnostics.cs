using System.Runtime.InteropServices;

namespace SimNetGateway.Engine;

public enum DiagnosticSeverity
{
    Info,
    Warning,
    Error,
}

/// <summary>One entry of the recent-diagnostics ring.</summary>
public readonly record struct DiagnosticEntry(
    double WallTime, DiagnosticSeverity Severity, string Category, string Origin, string Message);

/// <summary>
/// Counters and a bounded ring of recent entries (DD-001 6.2, 5.2). The success path only increments counters;
/// strings are built on warnings and errors.
/// </summary>
public sealed class Diagnostics
{
    private readonly DiagnosticEntry[] _ring;
    private readonly Func<double> _wallTime;
    private readonly Dictionary<Type, long> _unhandledByType = new();
    private readonly Dictionary<EventTypeId, long> _noSubscriberByType = new();
    private int _head;
    private int _count;

    internal Diagnostics(int capacity, Func<double> wallTime)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(capacity);
        _ring = new DiagnosticEntry[capacity];
        _wallTime = wallTime;
    }

    /// <summary>Ingress messages that reached a handler list (handled, aborted or rejected).</summary>
    public long MessagesProcessed { get; internal set; }

    /// <summary>Messages of a type nobody handles.</summary>
    public long UnhandledMessages { get; internal set; }

    /// <summary>Aborted, failed (exception) or rejected transactions.</summary>
    public long TransactionFailures { get; internal set; }

    public long RuleFailures { get; internal set; }

    public long CascadeOverflows { get; internal set; }

    public long SessionRejections { get; internal set; }

    public long EventsWithoutSubscriber { get; internal set; }

    public int Capacity => _ring.Length;

    public int Count => _count;

    public long UnhandledCount(Type messageType) => _unhandledByType.TryGetValue(messageType, out var n) ? n : 0;

    public long EventsWithoutSubscriberCount(EventTypeId type) => _noSubscriberByType.TryGetValue(type, out var n) ? n : 0;

    /// <summary>Recent entries, oldest first.</summary>
    public Enumerator Recent => new(this);

    public void Info(string origin, string message) => Add(DiagnosticSeverity.Info, "Module", origin, message);

    public void Warn(string origin, string message) => Add(DiagnosticSeverity.Warning, "Module", origin, message);

    public void Error(string origin, string message) => Add(DiagnosticSeverity.Error, "Module", origin, message);

    internal void Add(DiagnosticSeverity severity, string category, string origin, string message)
    {
        var entry = new DiagnosticEntry(_wallTime(), severity, category, origin, message);
        if (_count == _ring.Length)
        {
            _ring[_head] = entry;
            _head = (_head + 1) % _ring.Length;
        }
        else
        {
            _ring[(_head + _count) % _ring.Length] = entry;
            _count++;
        }
    }

    internal void CountUnhandled(Type messageType)
    {
        UnhandledMessages++;
        CollectionsMarshal.GetValueRefOrAddDefault(_unhandledByType, messageType, out _)++;
    }

    internal void CountNoSubscriber(EventTypeId type)
    {
        EventsWithoutSubscriber++;
        CollectionsMarshal.GetValueRefOrAddDefault(_noSubscriberByType, type, out _)++;
    }

    internal void RuleFailed(string ruleName, string message)
    {
        RuleFailures++;
        Add(DiagnosticSeverity.Error, "Rule", ruleName, message);
    }

    internal void TransactionFailed(string origin, string message)
    {
        TransactionFailures++;
        Add(DiagnosticSeverity.Warning, "Transaction", origin, message);
    }

    internal void CascadeOverflow(string origin, string message)
    {
        CascadeOverflows++;
        Add(DiagnosticSeverity.Warning, "Cascade", origin, message);
    }

    internal void SessionRejected(string origin, string message)
    {
        SessionRejections++;
        Add(DiagnosticSeverity.Warning, "Session", origin, message);
    }

    private DiagnosticEntry EntryAt(int offsetFromOldest) => _ring[(_head + offsetFromOldest) % _ring.Length];

    public struct Enumerator
    {
        private readonly Diagnostics _owner;
        private int _offset;

        internal Enumerator(Diagnostics owner)
        {
            _owner = owner;
            _offset = -1;
        }

        public readonly DiagnosticEntry Current => _owner.EntryAt(_offset);

        public readonly Enumerator GetEnumerator() => this;

        public bool MoveNext() => ++_offset < _owner._count;
    }
}
