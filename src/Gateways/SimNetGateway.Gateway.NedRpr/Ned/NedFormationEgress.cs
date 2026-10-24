using SimNetGateway.Engine;
using SimNetGateway.Protocol.Ned;

namespace SimNetGateway.Gateway.NedRpr;

/// <summary>What the individual NED egress modules and the formation egress agree on: which entities are parts of a structure.</summary>
internal static class NedStructure
{
    /// <summary>A member of a unit: its unit's formation message describes it, never an individual NED id.</summary>
    public static bool IsMember(PivotSchema schema, EntityRecord entity) => entity.Has(schema.Parent);

    /// <summary>A member or a unit (an entity with a member list): neither is published to NED as an individual.</summary>
    public static bool IsStructural(PivotSchema schema, EntityRecord entity) =>
        entity.Has(schema.Parent) || entity.Has(schema.AggregateMemberIds);
}

/// <summary>
/// N:1 towards NED (R-041, R-042, DD-001 11.9): for units of another origin than NED (an RPR aggregate with its member
/// platforms), a change of any member attribute that a formation member carries, a member created, removed, linked or
/// unlinked, marks the member's unit dirty (rule "MarkDirty"); at the end of the change set (rule "Flush") ONE
/// <see cref="NedFormation"/> per dirty unit is sent with all its current members, ordered by member index. The unit's NED id
/// is allocated on the first formation message (exhaustion warns once, the next change retries), the member keys are bound in
/// ned.member at once so that NED events naming (formation, member) resolve. A unit without members sends nothing until it has
/// some; a unit that had members sends an empty formation when it loses the last one. A removed unit that NED knows is
/// disposed (rule "Dispose"); a removed member makes its unit dirty.
/// </summary>
public sealed class NedFormationEgress : IGatewayModule
{
    private struct UnitState
    {
        public bool Dirty;
        public bool Sent;
        public bool Warned;
    }

    private readonly PivotSchema _schema;
    private readonly NedRprMaps _maps;
    private readonly SourceId _ned;
    private readonly IOutboundChannel<INedMessage> _output;
    private readonly List<EntityId> _dirty = new();
    private readonly List<EntityId> _flushing = new();
    private readonly List<EntityRecord> _found = new();
    private readonly List<EntityRecord> _members = new();
    private EntityTable<UnitState> _units = null!;
    private IGatewayContext _gateway = null!;

    public NedFormationEgress(PivotSchema schema, NedRprMaps maps, NedRprSources sources, IOutboundChannel<INedMessage> output)
    {
        _schema = schema;
        _maps = maps;
        _ned = sources.Ned;
        _output = output;
    }

    public string Name => nameof(NedFormationEgress);

    public void Register(ModuleSetup setup)
    {
        _gateway = setup.Gateway;
        _units = setup.CreateEntityTable<UnitState>("NedFormationEgress.Units");
        setup.Consumes(_schema.Name, _schema.TypeTkb, _schema.Force, _schema.Position, _schema.Orientation, _schema.Velocity,
            _schema.Damage, _schema.MemberIndex, _schema.AggregateMemberIds);
        setup.OnAttributesChanged(_schema.Parent, _schema.MemberIndex, _schema.Position, _schema.Orientation, _schema.Velocity,
                _schema.Damage, _schema.TypeTkb, _schema.Name, _schema.Force, _schema.AggregateMemberIds)
            .Named("MarkDirty")
            .When(t => t.Entity.Origin != _ned)
            .Do(MarkDirty);
        setup.OnChangeSetEnd().Named("Flush").Do(_ => Flush());
        setup.OnEntityRemoved()
            .Named("Dispose")
            .When(t => t.Entity.Origin != _ned)
            .Do(OnRemoved);
    }

    // ---- marking ----------------------------------------------------------------------------------------------------

    private void MarkDirty(EntityTrigger t)
    {
        var entity = t.Entity;
        if (entity.TryGet(_schema.Parent, out var parent))
        {
            Mark(parent.AsEntity());
        }

        if (t.Changed(_schema.Parent) && t.Delta.TryGetChange(_schema.Parent, out var change))
        {
            // A member that was moved to another unit, or taken out of its unit: the old unit changes, and the member
            // is no longer known to NED under the old (formation, index) key.
            if (!change.Old.IsNone && (change.New.IsNone || change.New.AsEntity() != change.Old.AsEntity()))
            {
                Mark(change.Old.AsEntity());
            }

            if (change.New.IsNone)
            {
                _maps.NedMember.Unbind(t.Id);
            }
        }

        if (entity.Has(_schema.AggregateMemberIds))
        {
            Mark(t.Id); // the unit itself (its name, type, force or member list changed)
        }
    }

    private void Mark(EntityId unit)
    {
        if (_gateway.State.Find(unit) is null)
        {
            return; // a unit that is gone has nothing to resend (and must not leave an entry in the table)
        }

        ref var state = ref _units.GetOrAdd(unit);
        if (!state.Dirty)
        {
            state.Dirty = true;
            _dirty.Add(unit);
        }
    }

    private void OnRemoved(EntityTrigger t)
    {
        var entity = t.Entity;
        if (entity.TryGet(_schema.Parent, out var parent))
        {
            Mark(parent.AsEntity()); // a member went away: its unit's formation changes
        }

        if (_units.TryGet(t.Id, out var state) && state.Sent && _maps.NedEntity.TryGetKey(t.Id, out ulong key))
        {
            _output.Send(new NedEntityDisposed(key)); // the key is still resolvable during the removal dispatch
        }
    }

    // ---- sending ----------------------------------------------------------------------------------------------------

    private void Flush()
    {
        if (_dirty.Count == 0)
        {
            return;
        }

        _flushing.Clear();
        _flushing.AddRange(_dirty);
        _dirty.Clear();
        for (int i = 0; i < _flushing.Count; i++)
        {
            var id = _flushing[i];
            if (_units.Contains(id))
            {
                _units.GetOrAdd(id).Dirty = false;
            }

            var unit = _gateway.State.Find(id);
            if (unit is not null && unit.IsAlive && unit.Origin != _ned)
            {
                SendFormation(unit);
            }
        }

        _flushing.Clear();
    }

    private void SendFormation(EntityRecord unit)
    {
        var members = _members;
        _gateway.State.Query().ReferencedBy(_schema.Parent, unit.Id).CopyTo(_found);
        members.Clear();
        for (int i = 0; i < _found.Count; i++)
        {
            var member = _found[i];
            if (member.Has(_schema.Position) && member.Has(_schema.MemberIndex))
            {
                members.Add(member); // NED cannot describe a member without a position
            }
        }

        _found.Clear();
        bool sent = _units.TryGet(unit.Id, out var known) && known.Sent;
        if (members.Count == 0 && !sent)
        {
            return;
        }

        var schema = _schema;
        members.Sort((a, b) => a.Get(schema.MemberIndex).AsInt().CompareTo(b.Get(schema.MemberIndex).AsInt()));
        if (!_maps.NedEntity.TryGetOrAllocate(unit.Id, out ulong unitKey))
        {
            ref var state = ref _units.GetOrAdd(unit.Id);
            if (!state.Warned)
            {
                state.Warned = true;
                _gateway.Diagnostics.Warn(Name,
                    $"NED id range exhausted: formation {unit.Id.Value} is not published to NED (retried on its next change).");
            }

            return;
        }

        BindMembers(members, unitKey);
        var wire = new NedFormationMember[members.Count];
        for (int i = 0; i < members.Count; i++)
        {
            wire[i] = ToWire(members[i]);
        }

        var force = NedForce.Unknown;
        if (unit.TryGet(_schema.Force, out var f) && NedRprConversions.TryForceFromPivot((int)f.AsInt(), out var mapped))
        {
            force = mapped;
        }

        string name = unit.TryGet(_schema.Name, out var n) ? n.AsString() : string.Empty;
        int type = unit.TryGet(_schema.TypeTkb, out var tkb) ? (int)tkb.AsInt() : 0;
        _output.Send(new NedFormation(unitKey, name, type, force, wire));
        _units.GetOrAdd(unit.Id).Sent = true;
        members.Clear();
    }

    /// <summary>Binds ned.member for every member: first frees the keys that change, then binds (members may swap indexes).</summary>
    private void BindMembers(List<EntityRecord> members, ulong unitKey)
    {
        var map = _maps.NedMember;
        for (int i = 0; i < members.Count; i++)
        {
            var key = new NedMemberKey(unitKey, (int)members[i].Get(_schema.MemberIndex).AsInt());
            if (map.TryGetKey(members[i].Id, out var current) && current != key)
            {
                map.Unbind(members[i].Id);
            }
        }

        for (int i = 0; i < members.Count; i++)
        {
            var key = new NedMemberKey(unitKey, (int)members[i].Get(_schema.MemberIndex).AsInt());
            if (map.TryResolve(key, out var owner) && owner != members[i].Id)
            {
                map.Unbind(owner); // a stale holder of the key (e.g. a member that was moved)
            }

            map.Bind(members[i].Id, key);
        }
    }

    private NedFormationMember ToWire(EntityRecord member)
    {
        var s = _schema;
        var orientation = member.TryGet(s.Orientation, out var o) ? o.AsVec3() : Vec3.Zero;
        var velocity = member.TryGet(s.Velocity, out var v) ? v.AsVec3() : Vec3.Zero;
        var geo = NedRprConversions.PivotToGeo(0, member.Get(s.Position).AsVec3(), orientation, velocity);
        int type = member.TryGet(s.TypeTkb, out var tkb) ? (int)tkb.AsInt() : 0;
        int damage = member.TryGet(s.Damage, out var d) ? (int)d.AsInt() : 0;
        return new NedFormationMember((int)member.Get(s.MemberIndex).AsInt(), geo.LatitudeDeg, geo.LongitudeDeg, geo.Altitude,
            geo.HeadingDeg, geo.PitchDeg, geo.RollDeg, geo.Speed, type, damage);
    }
}
