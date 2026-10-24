namespace SimNetGateway.Engine;

/// <summary>Gateway-internal entity id; never leaves the process. Real ids start at 1.</summary>
public readonly record struct EntityId(int Value)
{
    public static EntityId None => default;
    public bool IsNone => Value == 0;
}

/// <summary>Attribute id allocated by <see cref="AttributeRegistry"/>. Real ids start at 1.</summary>
public readonly record struct AttrId(int Value)
{
    public static AttrId None => default;
    public bool IsNone => Value == 0;
}

/// <summary>Simulation event type id allocated by <see cref="EventTypeRegistry"/>. Real ids start at 1.</summary>
public readonly record struct EventTypeId(int Value)
{
    public static EventTypeId None => default;
    public bool IsNone => Value == 0;
}

/// <summary>A side, a processor or the engine itself. Real ids start at 1.</summary>
public readonly record struct SourceId(int Value)
{
    public static SourceId None => default;
    public bool IsNone => Value == 0;
}

/// <summary>Identifies one input unit of work (one transaction). Real ids start at 1.</summary>
public readonly record struct CorrelationId(long Value)
{
    public static CorrelationId None => default;
    public bool IsNone => Value == 0;
}
