using SimNetGateway.Engine;
using SimNetGateway.Protocol.Ned;
using SimNetGateway.Protocol.Rpr;

namespace SimNetGateway.Gateway.NedRpr.Tests;

/// <summary>Message builders and queries of the stage 7a tests (formations, aggregates, split fire).</summary>
internal static class S
{
    public const ulong ViperId = 7;

    /// <summary>A formation member on a road: index i is 20 m east of index i-1.</summary>
    public static NedFormationMember Member(int index, int tkb = 101, int damage = 0, double lat = 50.0, double lonBase = 14.0,
        double speed = 10.0) =>
        new(index, lat, lonBase + index * 0.0003, 300.0, 90.0, 0.0, 0.0, speed, tkb, damage);

    public static NedFormation Formation(ulong id, string name, params NedFormationMember[] members) =>
        new(id, name, 100, NedForce.Friendly, members);

    public static NedFormation Viper(params int[] indexes) =>
        Formation(ViperId, "Viper", indexes.Select(i => Member(i)).ToArray());

    public static DisEntityId Dis(int number) => new(9, 9, (ushort)number);

    public static RprAggregateEntity Aggregate(uint handle, string marking, params int[] memberNumbers) =>
        new(new RprObjectHandle(handle), marking, T.Dis(400), RprForceId.Opposing, memberNumbers.Select(Dis).ToArray());

    /// <summary>A red platform with DIS entity number <paramref name="number"/> and handle 0x600 + number.</summary>
    public static RprObjectUpdate Platform(int number, string? marking = null, double lonOffset = 0, double speed = 5)
    {
        return T.RprFull((uint)(0x600 + number), marking ?? ("R" + number), 400, 50.2, 14.2 + lonOffset + number * 0.0002, 200, 270,
            speed, (ushort)number);
    }

    public static RprObjectHandle PlatformHandle(int number) => new((uint)(0x600 + number));

    /// <summary>The live members of a unit by the reverse index, ordered by member index.</summary>
    public static List<EntityRecord> MembersOf(GatewayHarness h, EntityRecord unit)
    {
        var list = new List<EntityRecord>();
        h.Engine.State.Query().ReferencedBy(h.Schema.Parent, unit.Id).CopyTo(list);
        list.Sort((a, b) => a.Get(h.Schema.MemberIndex).AsInt().CompareTo(b.Get(h.Schema.MemberIndex).AsInt()));
        return list;
    }

    /// <summary>The members of a unit by a full scan of all entities (the reference for the reverse index).</summary>
    public static List<EntityRecord> MembersByScan(GatewayHarness h, EntityRecord unit)
    {
        var list = new List<EntityRecord>();
        foreach (var e in h.Engine.State.Entities)
        {
            if (e.TryGet(h.Schema.Parent, out var p) && p.AsEntity() == unit.Id)
            {
                list.Add(e);
            }
        }

        list.Sort((a, b) => a.Get(h.Schema.MemberIndex).AsInt().CompareTo(b.Get(h.Schema.MemberIndex).AsInt()));
        return list;
    }

    public static EntityRecord NedUnit(GatewayHarness h, ulong id) =>
        h.EntityOfNed(id) ?? throw new InvalidOperationException("no NED entity " + id);

    public static EntityRecord NedMember(GatewayHarness h, ulong formation, int index) =>
        h.Host.NedMembers.TryResolve(new NedMemberKey(formation, index), out var e) && h.Engine.State.Find(e) is { } record
            ? record
            : throw new InvalidOperationException($"no member {index} of {formation}");

    public static EntityRecord RprEntity(GatewayHarness h, uint handle) =>
        h.EntityOfHandle(new RprObjectHandle(handle)) ?? throw new InvalidOperationException("no RPR entity " + handle);

    /// <summary>The records committed since <paramref name="skip"/> records ago that created at least one entity.</summary>
    public static List<ChangeSetRecord> CreatingRecordsSince(GatewayHarness h, int skip)
    {
        var all = h.Records();
        var result = new List<ChangeSetRecord>();
        for (int i = skip; i < all.Count; i++)
        {
            foreach (var delta in all[i].Entities)
            {
                if (delta.Created)
                {
                    result.Add(all[i]);
                    break;
                }
            }
        }

        return result;
    }

    /// <summary>The number of retained journal records committed by the source.</summary>
    public static int RecordsFrom(GatewayHarness h, SourceId source)
    {
        int n = 0;
        foreach (var record in h.Records())
        {
            if (record.Source == source)
            {
                n++;
            }
        }

        return n;
    }

    public static int CreatedIn(ChangeSetRecord record)
    {
        int n = 0;
        foreach (var delta in record.Entities)
        {
            if (delta.Created)
            {
                n++;
            }
        }

        return n;
    }
}
