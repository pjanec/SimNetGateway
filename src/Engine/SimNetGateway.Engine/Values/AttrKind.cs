namespace SimNetGateway.Engine;

/// <summary>Kind of an attribute value. <c>None</c> is the kind of an absent value (<c>default(AttrValue)</c>).</summary>
public enum AttrKind
{
    None = 0,
    Bool,
    Int,
    Double,
    Vec3,
    String,
    EntityRef,
    Object,
}
