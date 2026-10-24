namespace SimNetGateway.Engine;

/// <summary>
/// A module declares everything in <see cref="Register"/>: message handlers and rules. The engine knows no
/// module types; ingress, egress and processor are conventions (DD-001 4.4).
/// </summary>
public interface IGatewayModule
{
    string Name { get; }

    void Register(ModuleSetup setup);
}
