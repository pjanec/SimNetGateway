using System.Text;

namespace SimNetGateway.Engine;

public enum ValidationSeverity
{
    Warning,
    Error,
}

/// <summary>One finding of the vocabulary validation. <c>Code</c> is a stable kebab-case id, <c>Subject</c> the attribute, event type or module concerned.</summary>
public sealed record ValidationIssue(ValidationSeverity Severity, string Code, string Subject, string Message)
{
    public override string ToString() => Severity + " [" + Code + "] " + Subject + ": " + Message;
}

/// <summary>Codes of <see cref="ValidationIssue"/>.</summary>
public static class ValidationCodes
{
    public const string AttributeNeverProduced = "attribute-consumed-never-produced";
    public const string AttributeNeverConsumed = "attribute-produced-never-consumed";
    public const string EventNeverProduced = "event-consumed-never-produced";
    public const string EventNeverConsumed = "event-produced-never-consumed";
    public const string UnknownAttribute = "unknown-attribute";
    public const string UnknownEventType = "unknown-event-type";
}

/// <summary>
/// Result of <see cref="GatewayEngine.Validate"/> (DD-001 3.5, R-037): warnings for attributes / event types that are
/// consumed but never produced or produced but never consumed, errors for ids that are not registered. Kind conflicts
/// cannot show up here: <see cref="AttributeRegistry"/> already rejects them when they are registered. Entity structure
/// is never checked (R-034).
/// </summary>
public sealed class ValidationReport
{
    private readonly string _dump;

    internal ValidationReport(List<ValidationIssue> errors, List<ValidationIssue> warnings, string dump)
    {
        Errors = errors;
        Warnings = warnings;
        _dump = dump;
    }

    public IReadOnlyList<ValidationIssue> Errors { get; }

    public IReadOnlyList<ValidationIssue> Warnings { get; }

    public bool HasErrors => Errors.Count > 0;

    /// <summary>The whole vocabulary: every attribute and event type with description, producer and consumer modules.</summary>
    public string Dump() => _dump;

    internal static string BuildDump(AttributeRegistry attributes, EventTypeRegistry eventTypes,
        Dictionary<int, SortedSet<string>> attrProducers, Dictionary<int, SortedSet<string>> attrConsumers,
        Dictionary<int, SortedSet<string>> eventProducers, Dictionary<int, SortedSet<string>> eventConsumers)
    {
        var sb = new StringBuilder();
        sb.Append("Attributes (").Append(attributes.Count).Append(")\n");
        foreach (var def in attributes)
        {
            sb.Append("  ").Append(def.Name).Append(" : ").Append(def.Kind);
            if (def.Options != AttrOptions.None)
            {
                sb.Append(" [").Append(def.Options);
                if ((def.Options & AttrOptions.SpatialIndex) != 0)
                {
                    sb.Append(", cell ").Append(def.CellSize.ToString(System.Globalization.CultureInfo.InvariantCulture));
                }

                sb.Append(']');
            }

            if (!string.IsNullOrEmpty(def.Description))
            {
                sb.Append(" - ").Append(def.Description);
            }

            sb.Append('\n');
            AppendModules(sb, "produced by", attrProducers, def.Id.Value);
            AppendModules(sb, "consumed by", attrConsumers, def.Id.Value);
        }

        sb.Append("Event types (").Append(eventTypes.Count).Append(")\n");
        for (int id = 1; id <= eventTypes.Count; id++)
        {
            sb.Append("  ").Append(eventTypes.NameOf(new EventTypeId(id)));
            string? description = eventTypes.DescriptionOf(new EventTypeId(id));
            if (!string.IsNullOrEmpty(description))
            {
                sb.Append(" - ").Append(description);
            }

            sb.Append('\n');
            AppendModules(sb, "produced by", eventProducers, id);
            AppendModules(sb, "consumed by", eventConsumers, id);
        }

        return sb.ToString();
    }

    private static void AppendModules(StringBuilder sb, string role, Dictionary<int, SortedSet<string>> map, int id)
    {
        sb.Append("      ").Append(role).Append(": ");
        sb.Append(map.TryGetValue(id, out var modules) ? string.Join(", ", modules) : "(nobody)");
        sb.Append('\n');
    }
}
