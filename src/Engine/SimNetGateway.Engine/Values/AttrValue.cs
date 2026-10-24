using System.Globalization;

namespace SimNetGateway.Engine;

/// <summary>
/// Tagged union of the supported attribute value kinds; never boxes. <c>default</c> is the absent value
/// (<see cref="AttrKind.None"/>). Bool, Int and EntityRef share the long field; Double uses Vec3.X;
/// String and Object use the reference field.
/// </summary>
public readonly struct AttrValue : IEquatable<AttrValue>
{
    private readonly AttrKind _kind;
    private readonly long _num;
    private readonly Vec3 _vec;
    private readonly object? _obj;

    private AttrValue(AttrKind kind, long num, Vec3 vec, object? obj)
    {
        _kind = kind;
        _num = num;
        _vec = vec;
        _obj = obj;
    }

    public static AttrValue None => default;

    public AttrKind Kind => _kind;
    public bool IsNone => _kind == AttrKind.None;

    public static AttrValue From(bool value) => new(AttrKind.Bool, value ? 1 : 0, default, null);
    public static AttrValue From(long value) => new(AttrKind.Int, value, default, null);
    public static AttrValue From(double value) => new(AttrKind.Double, 0, new Vec3(value, 0, 0), null);
    public static AttrValue From(Vec3 value) => new(AttrKind.Vec3, 0, value, null);
    public static AttrValue From(EntityId value) => new(AttrKind.EntityRef, value.Value, default, null);

    public static AttrValue From(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        return new AttrValue(AttrKind.String, 0, default, value);
    }

    /// <summary>An immutable reference value (e.g. a weapon list); change detection is by <c>Equals</c>.</summary>
    public static AttrValue FromObject(object value)
    {
        ArgumentNullException.ThrowIfNull(value);
        return new AttrValue(AttrKind.Object, 0, default, value);
    }

    public bool AsBool() { Expect(AttrKind.Bool); return _num != 0; }
    public long AsInt() { Expect(AttrKind.Int); return _num; }
    public double AsDouble() { Expect(AttrKind.Double); return _vec.X; }
    public Vec3 AsVec3() { Expect(AttrKind.Vec3); return _vec; }
    public string AsString() { Expect(AttrKind.String); return (string)_obj!; }
    public EntityId AsEntity() { Expect(AttrKind.EntityRef); return new EntityId((int)_num); }
    public T AsObject<T>() { Expect(AttrKind.Object); return (T)_obj!; }

    private void Expect(AttrKind kind)
    {
        if (_kind != kind)
        {
            ThrowKind(kind);
        }
    }

    private void ThrowKind(AttrKind wanted) =>
        throw new InvalidOperationException($"AttrValue holds {_kind}, not {wanted}.");

    public bool Equals(AttrValue other)
    {
        if (_kind != other._kind)
        {
            return false;
        }

        switch (_kind)
        {
            case AttrKind.None:
                return true;
            case AttrKind.Bool:
            case AttrKind.Int:
            case AttrKind.EntityRef:
                return _num == other._num;
            case AttrKind.Double:
                return _vec.X.Equals(other._vec.X);
            case AttrKind.Vec3:
                return _vec.X.Equals(other._vec.X) && _vec.Y.Equals(other._vec.Y) && _vec.Z.Equals(other._vec.Z);
            case AttrKind.String:
                return string.Equals((string?)_obj, (string?)other._obj, StringComparison.Ordinal);
            case AttrKind.Object:
                return Equals(_obj, other._obj);
            default:
                return false;
        }
    }

    public override bool Equals(object? obj) => obj is AttrValue other && Equals(other);

    public override int GetHashCode()
    {
        switch (_kind)
        {
            case AttrKind.None:
                return 0;
            case AttrKind.Bool:
            case AttrKind.Int:
            case AttrKind.EntityRef:
                return HashCode.Combine((int)_kind, _num);
            case AttrKind.Double:
                return HashCode.Combine((int)_kind, _vec.X);
            case AttrKind.Vec3:
                return HashCode.Combine((int)_kind, _vec.X, _vec.Y, _vec.Z);
            case AttrKind.String:
                return HashCode.Combine((int)_kind, string.GetHashCode((string)_obj!, StringComparison.Ordinal));
            default:
                return HashCode.Combine((int)_kind, _obj);
        }
    }

    public static bool operator ==(AttrValue left, AttrValue right) => left.Equals(right);
    public static bool operator !=(AttrValue left, AttrValue right) => !left.Equals(right);

    public override string ToString()
    {
        var ci = CultureInfo.InvariantCulture;
        switch (_kind)
        {
            case AttrKind.None: return "None";
            case AttrKind.Bool: return _num != 0 ? "Bool(true)" : "Bool(false)";
            case AttrKind.Int: return "Int(" + _num.ToString(ci) + ")";
            case AttrKind.Double: return "Double(" + _vec.X.ToString("R", ci) + ")";
            case AttrKind.Vec3:
                return "Vec3(" + _vec.X.ToString("R", ci) + ", " + _vec.Y.ToString("R", ci) + ", " + _vec.Z.ToString("R", ci) + ")";
            case AttrKind.String: return "String(\"" + (string)_obj! + "\")";
            case AttrKind.EntityRef: return "EntityRef(" + _num.ToString(ci) + ")";
            default: return "Object(" + _obj + ")";
        }
    }
}
