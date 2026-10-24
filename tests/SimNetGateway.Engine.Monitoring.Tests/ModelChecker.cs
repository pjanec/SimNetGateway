using SimNetGateway.Engine;

namespace SimNetGateway.Engine.Monitoring.Tests;

/// <summary>Remembers the rule counters at every refresh to compute the expected rates independently of the model.</summary>
internal sealed class RuleOracle
{
    private readonly List<(double Time, long[] Fires, long[] Rejects, long[] Failures)> _history = new();
    private readonly double _window;
    private readonly int _size;

    public RuleOracle(double window, int size)
    {
        _window = window;
        _size = size;
    }

    public List<RuleRow> Expect(GatewayEngine e, RuleFilter filter)
    {
        var rules = e.Rules;
        double now = e.WallTime;
        _history.Add((now, rules.Select(r => r.FireCount).ToArray(), rules.Select(r => r.ConditionRejects).ToArray(),
            rules.Select(r => r.Failures).ToArray()));
        var recent = _history.Skip(Math.Max(0, _history.Count - _size)).ToList();
        var baseline = recent.First(h => h.Time >= now - _window);
        double dt = now - baseline.Time;
        var rows = new List<RuleRow>();
        for (int i = 0; i < rules.Count; i++)
        {
            var r = rules[i];
            if (filter.Failed && r.Failures == 0) continue;
            if (filter.Rejecting && r.ConditionRejects == 0) continue;
            if (filter.NeverFired && r.FireCount != 0) continue;
            if (filter.Text is { Length: > 0 } text
                && !r.FullName.Contains(text, StringComparison.OrdinalIgnoreCase)
                && !r.Trigger.Contains(text, StringComparison.OrdinalIgnoreCase)) continue;
            rows.Add(new RuleRow(r.RegistrationIndex, r.Module, r.Name, r.FullName, r.Trigger, r.Enabled, r.FireCount, r.ConditionRejects,
                r.Failures, dt > 0 ? (r.FireCount - baseline.Fires[i]) / dt : 0, dt > 0 ? (r.ConditionRejects - baseline.Rejects[i]) / dt : 0,
                dt > 0 ? (r.Failures - baseline.Failures[i]) / dt : 0));
        }

        return rows;
    }
}

/// <summary>Checks the small models (rules, identities, diagnostics, vocabulary) against brute-force values.</summary>
internal static class SmallModels
{
    public static void AssertRules(IReadOnlyList<RuleRow> actual, List<RuleRow> expected, string context)
    {
        Assert.True(actual.SequenceEqual(expected), $"{context}: rule rows differ ({actual.Count} vs {expected.Count})");
    }

    public static void AssertIdentities(GatewayEngine e, IdentityModel model, Func<IIdentityMap, AllocatorInfo?>? hook, string context)
    {
        var ids = new List<EntityId>();
        foreach (var x in e.State.Entities)
        {
            ids.Add(x.Id);
        }

        var expected = e.Identities.All.Select(m => new IdentityMapRow(m.Name, e.Sources.NameOf(m.Side), m.KeyType.Name,
            ids.Count(id => Oracle.KeyOf(m, id) is not null), hook?.Invoke(m))).ToList();
        Assert.True(expected.SequenceEqual(model.Maps), $"{context}: identity maps differ");
    }

    public static void AssertDiagnostics(GatewayEngine e, DiagnosticsModel model, string context)
    {
        var d = e.Diagnostics;
        Assert.Equal(new DiagnosticsCounters(d.MessagesProcessed, d.UnhandledMessages, d.TransactionFailures, d.RuleFailures,
            d.CascadeOverflows, d.SessionRejections, d.EventsWithoutSubscriber, d.Count, d.Capacity), model.Counters);
        var all = new List<DiagnosticEntry>();
        foreach (var entry in d.Recent)
        {
            all.Add(entry);
        }

        string? text = string.IsNullOrEmpty(model.Text) ? null : model.Text;
        var expected = all.Where(x => x.Severity >= model.MinSeverity
            && (text is null || x.Category.Contains(text, StringComparison.OrdinalIgnoreCase)
                || x.Origin.Contains(text, StringComparison.OrdinalIgnoreCase) || x.Message.Contains(text, StringComparison.OrdinalIgnoreCase))).ToList();
        Assert.True(expected.SequenceEqual(model.Entries), $"{context}: diagnostics entries differ ({model.Entries.Count} vs {expected.Count})");
    }

    public static void AssertVocabulary(GatewayEngine e, VocabularyModel model, string context)
    {
        var report = e.Validate();
        Assert.True(report.Errors.SequenceEqual(model.Errors), $"{context}: vocabulary errors differ");
        Assert.True(report.Warnings.SequenceEqual(model.Warnings), $"{context}: vocabulary warnings differ");
        Assert.Equal(report.Dump(), model.Dump);
    }
}
