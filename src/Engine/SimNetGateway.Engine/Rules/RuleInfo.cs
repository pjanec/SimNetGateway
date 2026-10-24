namespace SimNetGateway.Engine;

/// <summary>Description and counters of one registered rule; <see cref="Enabled"/> can be toggled at runtime.</summary>
public sealed class RuleInfo
{
    internal RuleInfo(string module, string name, string trigger, int registrationIndex)
    {
        Module = module;
        Name = name;
        FullName = module + "/" + name;
        Trigger = trigger;
        RegistrationIndex = registrationIndex;
    }

    public string Module { get; }
    public string Name { get; }

    /// <summary>"Module/Name"; unique.</summary>
    public string FullName { get; }

    /// <summary>The subscription, e.g. "OnAttributesChanged(Spatial.Position, Spatial.Velocity)".</summary>
    public string Trigger { get; }

    /// <summary>Position in the global registration order (the order in which rules fire).</summary>
    public int RegistrationIndex { get; }

    /// <summary>A disabled rule is skipped silently and its counters freeze.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>Times the action ran to completion.</summary>
    public long FireCount { get; internal set; }

    /// <summary>Times a <c>When</c> condition returned false.</summary>
    public long ConditionRejects { get; internal set; }

    /// <summary>Times a condition or the action threw.</summary>
    public long Failures { get; internal set; }

    public override string ToString() => FullName;
}
