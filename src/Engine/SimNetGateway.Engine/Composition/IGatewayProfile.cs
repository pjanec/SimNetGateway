namespace SimNetGateway.Engine;

/// <summary>
/// A gateway profile (DD-003 4): the composition of one gateway, i.e. which schema, sources, identity maps and modules
/// it registers and which inbound / outbound ports it requires from the host. It does no I/O; the host binds the ports.
/// </summary>
public interface IGatewayProfile
{
    string Name { get; }

    /// <summary>Registers everything of this gateway on the builder (schema, sources, modules, required ports).</summary>
    void Configure(GatewayBuilder builder);
}

/// <summary>
/// Settings of a gateway, supplied by the host (defaults, a file, command line, ...). A profile defines a derived
/// record with its own settings; this base only carries the operator rule overrides (rule full names "Module/Name").
/// </summary>
public abstract record GatewaySettings
{
    /// <summary>Rules to disable after the profile registered them.</summary>
    public IReadOnlyList<string> DisabledRules { get; init; } = [];

    /// <summary>Rules to enable after the profile registered them (applied after <see cref="DisabledRules"/>).</summary>
    public IReadOnlyList<string> EnabledRules { get; init; } = [];
}

/// <summary>A named profile with its settings; <see cref="CreateBuilder"/> runs the profile.</summary>
public sealed record GatewayConfiguration(string Name, IGatewayProfile Profile, GatewaySettings Settings)
{
    /// <summary>
    /// Creates a builder with these settings and lets the profile configure it; the host then binds the ports and
    /// calls <see cref="GatewayBuilder.Build"/>.
    /// </summary>
    public GatewayBuilder CreateBuilder(IClock clock, GatewayOptions? options = null)
    {
        var builder = new GatewayBuilder(clock, Settings, options);
        builder.Profile = Profile;
        Profile.Configure(builder);
        return builder;
    }
}
