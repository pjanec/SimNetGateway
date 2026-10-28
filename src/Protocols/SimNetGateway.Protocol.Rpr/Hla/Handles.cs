namespace SimNetGateway.Protocol.Rpr.Hla;

/// <summary>Common shape of the RTI handles (IEEE 1516.1-2010 clause 3.1): an opaque value with identity semantics.</summary>
public interface IHandle
{
    /// <summary>The numeric value, assigned by the RTI. 0 is never a valid handle.</summary>
    int Value { get; }
}

/// <summary>Handle of a joined federate (1516e <c>FederateHandle</c>).</summary>
public readonly record struct FederateHandle(int Value) : IHandle
{
    public override string ToString() => "Federate#" + Value;
}

/// <summary>Handle of an object class (1516e <c>ObjectClassHandle</c>).</summary>
public readonly record struct ObjectClassHandle(int Value) : IHandle
{
    public override string ToString() => "ObjectClass#" + Value;
}

/// <summary>Handle of an attribute; inherited attributes keep the handle of the class that declares them.</summary>
public readonly record struct AttributeHandle(int Value) : IHandle
{
    public override string ToString() => "Attribute#" + Value;
}

/// <summary>Handle of an interaction class (1516e <c>InteractionClassHandle</c>).</summary>
public readonly record struct InteractionClassHandle(int Value) : IHandle
{
    public override string ToString() => "InteractionClass#" + Value;
}

/// <summary>Handle of an interaction parameter; inherited parameters keep the handle of the declaring class.</summary>
public readonly record struct ParameterHandle(int Value) : IHandle
{
    public override string ToString() => "Parameter#" + Value;
}

/// <summary>Handle of an object instance (1516e <c>ObjectInstanceHandle</c>).</summary>
public readonly record struct ObjectInstanceHandle(int Value) : IHandle
{
    public override string ToString() => "ObjectInstance#" + Value;
}
