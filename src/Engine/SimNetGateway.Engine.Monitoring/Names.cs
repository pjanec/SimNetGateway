namespace SimNetGateway.Engine.Monitoring;

/// <summary>Name lookups that never throw (a monitor shows "source#7" rather than failing on an unknown id).</summary>
internal static class Names
{
    public static string Source(GatewayEngine engine, SourceId id)
    {
        if (id.IsNone)
        {
            return "(none)";
        }

        try
        {
            return engine.Sources.NameOf(id);
        }
        catch (KeyNotFoundException)
        {
            return "source#" + id.Value;
        }
    }

    public static string Attribute(GatewayEngine engine, AttrId id)
    {
        if (id.IsNone)
        {
            return "(none)";
        }

        return engine.Attributes.TryGet(id, out var def) ? def.Name : "attr#" + id.Value;
    }

    public static string EventType(GatewayEngine engine, EventTypeId id)
    {
        if (id.IsNone)
        {
            return "(none)";
        }

        try
        {
            return engine.EventTypes.NameOf(id);
        }
        catch (KeyNotFoundException)
        {
            return "event#" + id.Value;
        }
    }

    public static string Source(SourceRegistry? sources, SourceId id)
    {
        if (id.IsNone)
        {
            return "(none)";
        }

        if (sources is null)
        {
            return "source#" + id.Value;
        }

        try
        {
            return sources.NameOf(id);
        }
        catch (KeyNotFoundException)
        {
            return "source#" + id.Value;
        }
    }
}
