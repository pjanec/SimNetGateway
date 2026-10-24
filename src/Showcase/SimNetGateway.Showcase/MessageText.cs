using System.Globalization;
using System.Text;
using SimNetGateway.Protocol.Display;
using SimNetGateway.Protocol.Ned;
using SimNetGateway.Protocol.Rpr;

namespace SimNetGateway.Showcase;

/// <summary>
/// One-line, culture-independent text of the messages of the three sides, for the timeline of the report. Deterministic: no
/// default <c>ToString</c> of records (arrays and floating-point precision would leak into the report).
/// </summary>
public static class MessageText
{
    public static string Format(object message) => message switch
    {
        NedEntityMaster m => F($"NedEntityMaster id={m.Id} name={m.Name} tkb={m.TkbType} force={m.Force}"),
        NedGeoSpatial m => F($"NedGeoSpatial id={m.Id} lat={m.LatitudeDeg:F6} lon={m.LongitudeDeg:F6} alt={m.Altitude:F1} hdg={m.HeadingDeg:F1} spd={m.Speed:F1}"),
        NedEntityDamage m => F($"NedEntityDamage id={m.Id} level={m.Level}"),
        NedWeaponStatus m => F($"NedWeaponStatus id={m.Id}{Member(m.Member)} munition={m.SelectedMunitionTkb} ammo={m.Ammo}"),
        NedEntityDisposed m => F($"NedEntityDisposed id={m.Id}"),
        NedWeaponFire m => F($"NedWeaponFire fire={Short(m.FireId)} shooter={m.Shooter}{Member(m.ShooterMember)} target={m.Target}{Member(m.TargetMember)} munition={m.MunitionTkb} qty={m.Quantity}"),
        NedDetonation m => F($"NedDetonation det={Short(m.DetonationId)} target={m.Target}{Member(m.TargetMember)} munition={m.MunitionTkb} result={m.Result} damage={m.TargetDamage}"),
        NedSimControl m => F($"NedSimControl {m.Kind}{(m.Scenario is null ? "" : " '" + m.Scenario + "'")}"),
        NedFormation m => Formation(m),
        NedFireAmmunition m => F($"NedFireAmmunition fire={Short(m.FireId)} munition={m.MunitionTkb} qty={m.Quantity}"),
        NedDamageNotification m => F($"NedDamageNotification id={m.Id}{Member(m.Member)} {m.OldLevel}->{m.NewLevel}"),
        NedIffStatus m => F($"NedIffStatus id={m.Id} mode={m.Mode}"),
        UnknownNedMessage m => F($"UnknownNedMessage code={m.Code}"),
        RprObjectUpdate m => Update(m),
        RprAggregateEntity m => F($"RprAggregateEntity h={H(m.Handle)} marking={m.Marking} type={Dis(m.EntityType)} force={m.ForceId} members=[{string.Join(", ", m.MemberIds.Select(Id))}]"),
        RprObjectRemoved m => F($"RprObjectRemoved h={H(m.Handle)}"),
        RprWeaponFire m => F($"RprWeaponFire fire={Short(m.EventId)} firing={Opt(m.FiringObject)} target={Opt(m.TargetObject)} munition={Dis(m.Munition)} qty={m.Quantity}"),
        RprMunitionDetonation m => F($"RprMunitionDetonation det={Short(m.EventId)} firing={Opt(m.FiringObject)} target={Opt(m.TargetObject)} munition={Dis(m.Munition)} result={m.Result}"),
        RprSimulationControl m => F($"RprSimulationControl {m.Kind}"),
        RprRegisterObject m => F($"RprRegisterObject token={m.Token}"),
        RprObjectRegistered m => F($"RprObjectRegistered token={m.Token} h={H(m.Handle)}"),
        DisplayEntityPosition m => F($"DisplayEntityPosition {m.Label} lat={m.LatitudeDeg:F6} lon={m.LongitudeDeg:F6} alt={m.Altitude:F1} hdg={m.HeadingDeg:F1}"),
        DisplayEntityRemoved m => F($"DisplayEntityRemoved {m.Label}"),
        _ => message.GetType().Name,
    };

    /// <summary>The type name of a message ("NedFormation").</summary>
    public static string TypeOf(object message) => message.GetType().Name;

    public static string H(RprObjectHandle handle) => string.Create(CultureInfo.InvariantCulture, $"0x{handle.Value:X}");

    public static string Id(DisEntityId id) => string.Create(CultureInfo.InvariantCulture, $"{id.Site}.{id.Application}.{id.Entity}");

    public static string Dis(DisEntityType t) =>
        string.Create(CultureInfo.InvariantCulture, $"{t.Kind}.{t.Domain}.{t.Country}.{t.Category}.{t.Subcategory}.{t.Specific}.{t.Extra}");

    /// <summary>The first 8 hex digits of a guid (the scripts use readable guids).</summary>
    public static string Short(Guid id) => id.ToString("N", CultureInfo.InvariantCulture)[..8];

    private static string F(FormattableString text) => text.ToString(CultureInfo.InvariantCulture);

    private static string Member(int member) => member >= 0 ? string.Create(CultureInfo.InvariantCulture, $"/m{member}") : string.Empty;

    private static string Opt(RprObjectHandle? handle) => handle is { } h ? H(h) : "none";

    private static string Formation(NedFormation m)
    {
        var sb = new StringBuilder();
        sb.Append(F($"NedFormation id={m.Id} name={m.Name} tkb={m.TkbType} force={m.Force} members=["));
        for (int i = 0; i < m.Members.Length; i++)
        {
            var member = m.Members[i];
            if (i > 0)
            {
                sb.Append("; ");
            }

            sb.Append(F($"{member.Index}: tkb={member.TkbType} lat={member.LatitudeDeg:F6} lon={member.LongitudeDeg:F6} spd={member.Speed:F1} dmg={member.Damage}"));
        }

        return sb.Append(']').ToString();
    }

    private static string Update(RprObjectUpdate m)
    {
        var sb = new StringBuilder();
        sb.Append(F($"RprObjectUpdate h={H(m.Handle)}"));
        if (m.Marking is not null)
        {
            sb.Append(F($" marking={m.Marking}"));
        }

        if (m.EntityType is { } type)
        {
            sb.Append(F($" type={Dis(type)}"));
        }

        if (m.ForceId is { } force)
        {
            sb.Append(F($" force={force}"));
        }

        if (m.EntityIdentifier is { } id)
        {
            sb.Append(F($" id={Id(id)}"));
        }

        if (m.WorldLocation is { } p)
        {
            sb.Append(F($" loc=({p.X:F1},{p.Y:F1},{p.Z:F1})"));
        }

        if (m.Orientation is not null)
        {
            sb.Append(" orientation");
        }

        if (m.VelocityVector is { } v)
        {
            sb.Append(F($" vel=({v.X:F1},{v.Y:F1},{v.Z:F1})"));
        }

        if (m.AccelerationVector is not null)
        {
            sb.Append(" accel");
        }

        if (m.AngularVelocity is not null)
        {
            sb.Append(" angvel");
        }

        if (m.DeadReckoningAlgorithm is { } dr)
        {
            sb.Append(F($" dr={dr}"));
        }

        if (m.DamageState is { } damage)
        {
            sb.Append(F($" damage={damage}"));
        }

        if (m.DamagePercent is { } percent)
        {
            sb.Append(F($" damagePercent={percent}"));
        }

        return sb.ToString();
    }
}
