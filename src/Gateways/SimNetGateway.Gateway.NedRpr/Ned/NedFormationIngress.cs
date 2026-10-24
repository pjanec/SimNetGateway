using SimNetGateway.Common;
using SimNetGateway.Engine;
using SimNetGateway.Protocol.Ned;

namespace SimNetGateway.Gateway.NedRpr;

/// <summary>
/// NedFormation (1:N, R-041, R-042): ONE transaction resolves or creates the unit (ned.entity, no position) and every member
/// (ned.member, key (formation id, index)), writes their attributes like the individual NED ingress modules do, links each
/// member to its unit (<c>Structure.Parent</c>, <c>Structure.MemberIndex</c>) and destroys the unit's members that the message no
/// longer lists - all or nothing. A formation whose unit is of another origin is our own publication looped back: the whole
/// message is ignored. Also destroys the members of a formation that NED disposes of.
/// </summary>
public sealed class NedFormationIngress : IGatewayModule
{
    private readonly PivotSchema _schema;
    private readonly NedRprMaps _maps;
    private readonly List<EntityRecord> _scratch = new();

    public NedFormationIngress(PivotSchema schema, NedRprMaps maps)
    {
        _schema = schema;
        _maps = maps;
    }

    public string Name => nameof(NedFormationIngress);

    public void Register(ModuleSetup setup)
    {
        setup.Produces(_schema.Name, _schema.TypeTkb, _schema.Force, _schema.Position, _schema.Orientation, _schema.Velocity,
            _schema.DrAlgorithm, _schema.StateTime, _schema.Damage, _schema.Parent, _schema.MemberIndex);
        setup.OnMessage<NedFormation>(Handle);
        setup.OnMessage<NedEntityDisposed>(HandleDisposed);
    }

    private void Handle(in NedFormation m, IngressContext ctx)
    {
        if (!NedRprConversions.TryForceToPivot(m.Force, out int force))
        {
            ctx.Abort($"force {(int)m.Force} out of range");
            return;
        }

        var members = m.Members ?? Array.Empty<NedFormationMember>();
        for (int i = 0; i < members.Length; i++)
        {
            if (members[i].Index < 0)
            {
                ctx.Abort($"formation {m.Id}: member index {members[i].Index} is negative");
                return;
            }

            if (!NedRprConversions.IsValidDamage(members[i].Damage))
            {
                ctx.Abort($"formation {m.Id}: damage level {members[i].Damage} of member {members[i].Index} out of range");
                return;
            }

            for (int j = 0; j < i; j++)
            {
                if (members[j].Index == members[i].Index)
                {
                    ctx.Abort($"formation {m.Id}: member index {members[i].Index} listed twice");
                    return;
                }
            }
        }

        var resolution = IngressEntities.Resolve(ctx, _maps.NedEntity, m.Id, create: true, out var unit);
        if (resolution == EntityResolution.Echo)
        {
            return;
        }

        var changes = ctx.Changes;
        changes.Set(unit, _schema.Name, AttrValue.From(m.Name));
        changes.Set(unit, _schema.TypeTkb, AttrValue.From(m.TkbType));
        changes.Set(unit, _schema.Force, AttrValue.From(force));

        for (int i = 0; i < members.Length; i++)
        {
            ref readonly var member = ref members[i];
            var key = new NedMemberKey(m.Id, member.Index);
            if (IngressEntities.Resolve(ctx, _maps.NedMember, key, create: true, out var id) == EntityResolution.Echo)
            {
                continue;
            }

            NedRprConversions.GeoToPivot(member.LatitudeDeg, member.LongitudeDeg, member.Altitude, member.HeadingDeg,
                member.PitchDeg, member.RollDeg, member.Speed, out var position, out var orientation, out var velocity);
            var algorithm = member.Speed > 0 ? DisDeadReckoning.Fpw : DisDeadReckoning.Static;
            changes.Set(id, _schema.Name, AttrValue.From(m.Name + "-" + member.Index));
            changes.Set(id, _schema.TypeTkb, AttrValue.From(member.TkbType));
            changes.Set(id, _schema.Force, AttrValue.From(force)); // a member has the force of its unit
            changes.Set(id, _schema.Position, AttrValue.From(position));
            changes.Set(id, _schema.Orientation, AttrValue.From(orientation));
            changes.Set(id, _schema.Velocity, AttrValue.From(velocity));
            changes.Set(id, _schema.DrAlgorithm, AttrValue.From((long)algorithm));
            changes.Set(id, _schema.StateTime, AttrValue.From(ctx.SimTime));
            changes.Set(id, _schema.Damage, AttrValue.From(member.Damage));
            changes.Set(id, _schema.Parent, AttrValue.From(unit));
            changes.Set(id, _schema.MemberIndex, AttrValue.From(member.Index));
        }

        if (resolution == EntityResolution.Existing)
        {
            DestroyUnlisted(ctx, unit, members);
        }
    }

    /// <summary>Destroys the committed members of the unit whose index the message does not list.</summary>
    private void DestroyUnlisted(IngressContext ctx, EntityId unit, NedFormationMember[] listed)
    {
        var scratch = _scratch;
        ctx.State.Query().ReferencedBy(_schema.Parent, unit).CopyTo(scratch);
        for (int i = 0; i < scratch.Count; i++)
        {
            var record = scratch[i];
            if (record.Origin != ctx.Source)
            {
                continue;
            }

            long index = record.TryGet(_schema.MemberIndex, out var v) ? v.AsInt() : -1;
            bool isListed = false;
            for (int j = 0; j < listed.Length && !isListed; j++)
            {
                isListed = listed[j].Index == index;
            }

            if (!isListed)
            {
                ctx.Changes.DestroyEntity(record.Id);
            }
        }

        scratch.Clear();
    }

    /// <summary>A formation that NED disposes of takes its members with it (the unit itself is destroyed by NedDisposeIngress).</summary>
    private void HandleDisposed(in NedEntityDisposed m, IngressContext ctx)
    {
        if (IngressEntities.Resolve(ctx, _maps.NedEntity, m.Id, create: false, out var unit) != EntityResolution.Existing)
        {
            return;
        }

        var scratch = _scratch;
        ctx.State.Query().ReferencedBy(_schema.Parent, unit).CopyTo(scratch);
        for (int i = 0; i < scratch.Count; i++)
        {
            if (scratch[i].Origin == ctx.Source)
            {
                ctx.Changes.DestroyEntity(scratch[i].Id);
            }
        }

        scratch.Clear();
    }
}
