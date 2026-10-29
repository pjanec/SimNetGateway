using SimNetGateway.Protocol.Rpr.Fom;

namespace SimNetGateway.Transport.HlaRpr;

/// <summary>How <see cref="RprHlaConnection"/> joins its federation (DD-008 section 5.1).</summary>
public sealed record RprHlaSettings
{
    public string FederationName { get; init; } = "RPR";

    /// <summary>The name this federate joins with; unique within the federation.</summary>
    public string FederateName { get; init; } = "SimNetGateway";

    public string FederateType { get; init; } = "Gateway";

    /// <summary>FOM module names handed to <c>CreateFederationExecution</c> (resolved by the RTI; the code registry of Protocol.Rpr for the fake RTI).</summary>
    public IReadOnlyList<string> FomModules { get; init; } = [RprFom2.ModuleName];

    /// <summary>Create the federation execution first (an existing one is not an error); false: join only.</summary>
    public bool CreateIfMissing { get; init; } = true;

    /// <summary>How long a fire / detonation event correlation is kept, seconds of the connection's clock (DD-008 section 3.4).</summary>
    public double EventCorrelationSeconds { get; init; } = 30;

    /// <summary>The most event correlations kept; the oldest are dropped first.</summary>
    public int EventCorrelationCapacity { get; init; } = 4096;
}
