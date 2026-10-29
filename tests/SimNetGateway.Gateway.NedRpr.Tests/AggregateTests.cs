using SimNetGateway.Engine;
using SimNetGateway.Protocol.Ned;
using SimNetGateway.Protocol.Rpr;

namespace SimNetGateway.Gateway.NedRpr.Tests;

/// <summary>DD-001 3.7 / DD-002 C4, C12 (R-041): an RPR aggregate with member platforms becomes ONE formation for NED.</summary>
public class AggregateTests
{
    private const uint Wolf = 0x700;

    private static GatewayHarness Showcase() => new(NedRprConfigurations.Showcase);

    /// <summary>Wolf with members 1..<paramref name="count"/> (platforms first, then the aggregate), output cleared.</summary>
    private static GatewayHarness WolfPack(int count = 3, int listed = 0)
    {
        listed = Math.Max(listed, count);
        var h = Showcase();
        for (int n = 1; n <= count; n++)
        {
            h.Rpr(S.Platform(n));
        }

        h.Step();
        h.RprStep(S.Aggregate(Wolf, "Wolf", Enumerable.Range(1, listed).ToArray()));
        h.Steps(2);
        h.ClearOutput();
        return h;
    }

    private static NedFormation LastFormation(GatewayHarness h) => h.NedSent<NedFormation>().Last();

    // ---- the aggregate arrives after its members ---------------------------------------------------------------------

    [Fact]
    public void Members_arriving_before_their_aggregate_are_individuals_then_withdrawn_and_NED_gets_one_formation()
    {
        var h = Showcase();
        h.Rpr(S.Platform(1));
        h.Rpr(S.Platform(2));
        h.Rpr(S.Platform(3));
        h.Step();

        // first they are individuals: master + geo-spatial per member, and no parent
        var introduced = h.NedSent<NedEntityMaster>();
        Assert.Equal(3, introduced.Count);
        var individualIds = introduced.Select(m => m.Id).ToList();
        Assert.Equal(3, h.NedSent<NedGeoSpatial>().Count);
        Assert.All(Enumerable.Range(1, 3), n => Assert.False(S.RprEntity(h, (uint)(0x600 + n)).Has(h.Schema.Parent)));
        h.ClearOutput();

        // the aggregate lists them and a member that does not exist yet (4)
        h.RprStep(S.Aggregate(Wolf, "Wolf", 1, 2, 3, 4));
        h.Steps(2);

        var disposed = h.NedSent<NedEntityDisposed>();
        Assert.Equal(individualIds.OrderBy(x => x), disposed.Select(d => d.Id).OrderBy(x => x)); // withdrawal, with the individual ids
        var formation = Assert.Single(h.NedSent<NedFormation>()); // ONE formation, not three messages
        Assert.Equal(3, formation.Members.Length);
        Assert.Equal("Wolf", formation.Name);
        Assert.Equal(400, formation.TkbType);
        Assert.Equal(NedForce.Opposing, formation.Force);
        Assert.Equal(new[] { 0, 1, 2 }, formation.Members.Select(m => m.Index)); // member list positions 0..2
        Assert.Empty(h.NedSent<NedEntityMaster>()); // the unit is not an individual
        var unit = S.RprEntity(h, Wolf);
        Assert.Equal(3, S.MembersOf(h, unit).Count);
        Assert.Equal(S.MembersOf(h, unit).Select(m => m.Id), S.MembersByScan(h, unit).Select(m => m.Id));
        // individual ids are released: only the unit has a ned.entity key now
        Assert.Equal(1, h.NedIds.Count);
        Assert.All(individualIds, id => Assert.False(h.NedIds.TryResolve(id, out _)));
        Assert.True(h.NedIds.TryResolve(formation.Id, out var unitEntity) && unitEntity == unit.Id);
        for (int i = 0; i < 3; i++)
        {
            Assert.True(h.Host.NedMembers.TryResolve(new NedMemberKey(formation.Id, i), out var e));
            Assert.Equal(S.RprEntity(h, (uint)(0x601 + i)).Id, e);
        }
    }

    [Fact]
    public void The_formation_carries_the_geodetic_state_type_and_damage_of_each_member()
    {
        var h = Showcase();
        h.Rpr(S.Platform(1));
        h.Rpr(S.Platform(2));
        h.RprStep(new RprObjectUpdate(S.PlatformHandle(2)) { DamageState = RprDamageState.ModerateDamage });
        h.RprStep(S.Aggregate(Wolf, "Wolf", 2, 1)); // list order decides the member index
        h.Steps(2);

        var formation = LastFormation(h);

        Assert.Equal(new[] { 0, 1 }, formation.Members.Select(m => m.Index));
        var first = formation.Members[0]; // platform 2
        var second = formation.Members[1]; // platform 1
        Assert.Equal(2, first.Damage);
        Assert.Equal(0, second.Damage);
        Assert.Equal(400, first.TkbType);
        Assert.Equal(270, first.HeadingDeg, 3);
        Assert.Equal(5, first.Speed, 3);
        Assert.Equal(50.2, first.LatitudeDeg, 6);
        Assert.Equal(14.2 + 2 * 0.0002, first.LongitudeDeg, 6);
        Assert.Equal(14.2 + 1 * 0.0002, second.LongitudeDeg, 6);
        Assert.Equal(200, first.Altitude, 2);
    }

    [Fact]
    public void An_unknown_member_id_linked_later_produces_exactly_one_updated_formation()
    {
        var h = WolfPack(3, listed: 4); // the aggregate names a 4th member that does not exist yet
        var wolfId = UnitId(h);

        h.RprStep(S.Platform(4)); // the platform the aggregate already names
        h.Steps(2);

        var formation = Assert.Single(h.NedSent<NedFormation>());
        Assert.Equal(wolfId, formation.Id);
        Assert.Equal(4, formation.Members.Length);
        Assert.Equal(new[] { 0, 1, 2, 3 }, formation.Members.Select(m => m.Index));
        // the late member was an individual for a moment (its master was sent when it appeared) and is withdrawn again
        var masters = h.NedSent<NedEntityMaster>();
        var disposed = h.NedSent<NedEntityDisposed>();
        Assert.Equal(masters.Select(m => m.Id), disposed.Select(d => d.Id));
        Assert.True(h.Host.NedMembers.TryResolve(new NedMemberKey(wolfId, 3), out var late));
        Assert.Equal(S.RprEntity(h, 0x604).Id, late);
        Assert.Equal(1, h.NedIds.Count);
    }

    private static ulong UnitId(GatewayHarness h) =>
        h.NedIds.TryGetKey(S.RprEntity(h, Wolf).Id, out ulong id) ? id : throw new InvalidOperationException("the unit has no NED id");

    [Fact]
    public void A_member_that_moves_makes_the_unit_resend_its_whole_formation_once()
    {
        var h = WolfPack(3);
        NedRprConversions.GeoToPivot(50.21, 14.3, 210, 180, 0, 0, 7, out var p, out var o, out var v);

        h.RprStep(new RprObjectUpdate(S.PlatformHandle(2))
        {
            WorldLocation = NedRprConversions.ToWorldLocation(p),
            Orientation = NedRprConversions.ToRprOrientation(o),
            VelocityVector = NedRprConversions.ToRprVector(v),
        });
        h.Steps(2);

        var formation = Assert.Single(h.NedSent<NedFormation>());
        Assert.Equal(3, formation.Members.Length); // all members, not only the one that moved
        var moved = formation.Members[1];
        Assert.Equal(50.21, moved.LatitudeDeg, 6);
        Assert.Equal(14.3, moved.LongitudeDeg, 6);
        Assert.Equal(7, moved.Speed, 3);
        Assert.Empty(h.NedSent<NedGeoSpatial>()); // never as an individual
    }

    [Fact]
    public void Several_member_updates_of_one_change_set_give_one_formation()
    {
        var h = WolfPack(3);

        // a re-sent aggregate re-links the same members without any change: no formation
        h.RprStep(S.Aggregate(Wolf, "Wolf", 1, 2, 3));
        h.Steps(2);
        Assert.Empty(h.NedSent<NedFormation>());

        // a new name of the unit changes the formation once
        h.RprStep(S.Aggregate(Wolf, "Wolf-B", 1, 2, 3));
        h.Steps(2);
        var formation = Assert.Single(h.NedSent<NedFormation>());
        Assert.Equal("Wolf-B", formation.Name);
    }

    [Fact]
    public void A_member_damage_reaches_NED_inside_the_formation_never_as_an_individual_descriptor()
    {
        var h = WolfPack(3);

        h.RprStep(new RprObjectUpdate(S.PlatformHandle(3)) { DamageState = RprDamageState.Destroyed });
        h.Steps(2);

        var formation = Assert.Single(h.NedSent<NedFormation>());
        Assert.Equal(3, formation.Members[2].Damage);
        Assert.Empty(h.NedSent<NedEntityDamage>());
    }

    [Fact]
    public void A_member_rename_or_retype_does_not_introduce_the_member_to_NED_as_an_individual()
    {
        var h = WolfPack(3);

        h.RprStep(new RprObjectUpdate(S.PlatformHandle(2)) { Marking = "Renamed", EntityType = T.Dis(100), ForceId = RprForceId.Neutral });
        h.Steps(2);

        Assert.Empty(h.NedSent<NedEntityMaster>());
        Assert.Empty(h.NedSent<NedGeoSpatial>());
        Assert.Equal(1, h.NedIds.Count);
        Assert.Equal(100, Assert.Single(h.NedSent<NedFormation>()).Members[1].TkbType);
    }

    // ---- members leaving -------------------------------------------------------------------------------------------

    [Fact]
    public void A_member_that_the_aggregate_no_longer_lists_is_unlinked_and_becomes_an_individual_again()
    {
        var h = WolfPack(3);
        var gone = S.RprEntity(h, 0x602);

        h.RprStep(S.Aggregate(Wolf, "Wolf", 1, 3));
        h.Steps(2);

        Assert.False(gone.Has(h.Schema.Parent));
        Assert.False(gone.Has(h.Schema.MemberIndex));
        var formation = Assert.Single(h.NedSent<NedFormation>());
        Assert.Equal(2, formation.Members.Length);
        Assert.Equal(new[] { 0, 1 }, formation.Members.Select(m => m.Index));
        Assert.False(h.Host.NedMembers.Contains(gone.Id)); // its (formation, index) key was released
        Assert.Equal(2, h.Host.NedMembers.Count);
        // NED learns it as an individual with a new id: master, geo-spatial
        var master = Assert.Single(h.NedSent<NedEntityMaster>());
        Assert.Equal("R2", master.Name);
        Assert.True(h.NedIds.TryResolve(master.Id, out var entity) && entity == gone.Id);
        Assert.Equal(2, S.MembersOf(h, S.RprEntity(h, Wolf)).Count);
    }

    [Fact]
    public void A_member_that_stops_existing_is_dropped_from_the_next_formation_and_the_last_member_leaves_an_empty_formation()
    {
        var h = WolfPack(2);
        var wolfId = UnitId(h);

        h.RprStep(new RprObjectRemoved(S.PlatformHandle(2)));
        h.Steps(2);
        var one = Assert.Single(h.NedSent<NedFormation>());
        Assert.Single(one.Members);
        Assert.Equal(0, one.Members[0].Index);
        Assert.Empty(h.NedSent<NedEntityDisposed>()); // a member's removal is told by the formation, not by a dispose

        h.ClearOutput();
        h.RprStep(new RprObjectRemoved(S.PlatformHandle(1)));
        h.Steps(2);
        var empty = Assert.Single(h.NedSent<NedFormation>());
        Assert.Equal(wolfId, empty.Id);
        Assert.Empty(empty.Members);
        Assert.Equal(0, h.Host.NedMembers.Count);
    }

    [Fact]
    public void Removing_the_unit_tells_NED_with_one_dispose_for_the_unit_id_and_frees_its_members()
    {
        var h = WolfPack(3);
        var wolfId = UnitId(h);

        h.RprStep(new RprObjectRemoved(new RprObjectHandle(Wolf)));
        h.Steps(2);

        Assert.Equal(wolfId, Assert.Single(h.NedSent<NedEntityDisposed>(), d => d.Id == wolfId).Id);
        Assert.False(h.NedIds.TryResolve(wolfId, out _));
        // the members survive as individuals again (unlinked)
        var masters = h.NedSent<NedEntityMaster>();
        Assert.Equal(3, masters.Count);
        Assert.Equal(3, h.NedIds.Count);
        Assert.All(Enumerable.Range(1, 3), n => Assert.False(S.RprEntity(h, (uint)(0x600 + n)).Has(h.Schema.Parent)));
        Assert.Equal(0, h.Host.NedMembers.Count);
    }

    [Fact]
    public void Unloading_leaves_nothing_behind_and_tells_NED_about_the_unit_once()
    {
        var h = WolfPack(3);
        var wolfId = UnitId(h);

        h.NedStep(new NedSimControl(NedControlKind.Load, "x"));
        h.NedStep(new NedSimControl(NedControlKind.Unload, null));
        h.Steps(2);

        Assert.Equal(0, h.Engine.State.Count);
        Assert.Single(h.NedSent<NedEntityDisposed>(), d => d.Id == wolfId);
        Assert.Empty(h.NedSent<NedFormation>());
        Assert.Equal(0, h.NedIds.Count);
        Assert.Equal(0, h.Host.NedMembers.Count);
    }

    // ---- linking rules -----------------------------------------------------------------------------------------------

    [Fact]
    public void A_listed_identifier_of_the_gateways_own_publication_is_not_linked()
    {
        var h = Showcase();
        T.PublishNed(h, T.Hawk, "Hawk", 200);
        h.RprEntityIds.TryGetKey(h.EntityOfNed(T.Hawk)!.Id, out var hawkDis);
        h.Rpr(S.Platform(1));
        h.Step();

        h.RprStep(new RprAggregateEntity(new RprObjectHandle(Wolf), "Wolf", T.Dis(400), RprForceId.Opposing, [hawkDis, S.Dis(1)]));
        h.Steps(2);

        Assert.False(h.EntityOfNed(T.Hawk)!.Has(h.Schema.Parent));
        var unit = S.RprEntity(h, Wolf);
        var member = Assert.Single(S.MembersOf(h, unit));
        Assert.Equal(S.RprEntity(h, 0x601).Id, member.Id);
        Assert.Equal(1, member.Get(h.Schema.MemberIndex).AsInt()); // its position in the list
    }

    [Fact]
    public void An_aggregate_for_a_known_handle_updates_the_unit_and_a_looped_back_one_of_ours_is_ignored()
    {
        var h = WolfPack(2);

        h.RprStep(S.Aggregate(Wolf, "Wolf", 1, 2) with { ForceId = RprForceId.Friendly });
        h.Steps(2);

        var formation = Assert.Single(h.NedSent<NedFormation>());
        Assert.Equal(NedForce.Friendly, formation.Force);
        Assert.Empty(h.RprSent<RprObjectUpdate>());
        Assert.Equal(3, h.Engine.State.Count);
    }

    [Fact]
    public void An_aggregate_with_an_unknown_DIS_type_uses_the_default_type_and_warns_once()
    {
        var h = Showcase();
        h.Checker.Expect("Unknown DIS entity type");

        h.Rpr(S.Platform(1));
        h.Step();
        var odd = new DisEntityType(9, 9, 9, 9, 9, 9, 9);
        h.RprStep(new RprAggregateEntity(new RprObjectHandle(Wolf), "Wolf", odd, RprForceId.Opposing, [S.Dis(1)]));
        h.RprStep(new RprAggregateEntity(new RprObjectHandle(Wolf), "Wolf", odd, RprForceId.Opposing, [S.Dis(1)]));
        h.Steps(2);

        Assert.Equal(1, h.Checker.WarningsContaining("Unknown DIS entity type"));
        Assert.Equal(TypeCatalog.DefaultTkb, S.RprEntity(h, Wolf).Get(h.Schema.TypeTkb).AsInt());
    }

    // ---- ignored, re-parented and incomplete members ------------------------------------------------------------------

    [Fact]
    public void A_NED_formation_that_names_the_id_of_an_RPR_origin_unit_is_ignored_whatever_it_says()
    {
        var h = WolfPack(2);
        var wolfId = UnitId(h);
        string snapshot = h.Snapshot();
        int entities = h.Engine.State.Count;

        h.NedStep(new NedFormation(wolfId, "Impostor", 777, NedForce.Neutral, [S.Member(0), S.Member(5)]));
        h.Steps(2);

        Assert.Equal(snapshot, h.Snapshot());
        Assert.Equal(entities, h.Engine.State.Count);
        Assert.Equal("Wolf", S.RprEntity(h, Wolf).Get(h.Schema.Name).AsString());
        Assert.Equal(0, h.NedOut.Count);
    }

    [Fact]
    public void An_aggregate_message_for_the_handle_of_the_gateways_own_object_is_ignored()
    {
        var h = Showcase();
        T.PublishNed(h, T.Hawk, "Hawk", 200);
        var hawkHandle = h.HandleOf(h.EntityOfNed(T.Hawk)!);
        string snapshot = h.Snapshot();

        h.RprStep(new RprAggregateEntity(hawkHandle, "Fake", T.Dis(400), RprForceId.Opposing, [S.Dis(1)]));
        h.Steps(2);

        Assert.Equal(snapshot, h.Snapshot());
        Assert.False(h.EntityOfNed(T.Hawk)!.Has(h.Schema.AggregateMemberIds));
    }

    [Fact]
    public void A_member_taken_over_by_another_aggregate_updates_both_formations_and_the_member_keys_follow()
    {
        var h = Showcase();
        h.Rpr(S.Platform(1));
        h.Rpr(S.Platform(2));
        h.Rpr(S.Platform(3));
        h.Step();
        h.RprStep(S.Aggregate(0x700, "A", 1, 2));
        h.RprStep(S.Aggregate(0x701, "B", 3));
        h.Steps(2);
        Assert.True(h.NedIds.TryGetKey(S.RprEntity(h, 0x700).Id, out ulong a));
        Assert.True(h.NedIds.TryGetKey(S.RprEntity(h, 0x701).Id, out ulong b));
        h.ClearOutput();

        h.RprStep(S.Aggregate(0x701, "B", 2, 3)); // platform 2 changes sides
        h.Steps(2);

        var formations = h.NedSent<NedFormation>();
        Assert.Equal(2, formations.Count);
        Assert.Equal(new[] { 0 }, formations.Single(f => f.Id == a).Members.Select(x => x.Index)); // A lost member 2
        Assert.Equal(new[] { 0, 1 }, formations.Single(f => f.Id == b).Members.Select(x => x.Index));
        Assert.True(h.Host.NedMembers.TryResolve(new NedMemberKey(b, 0), out var moved));
        Assert.Equal(S.RprEntity(h, 0x602).Id, moved);
        Assert.False(h.Host.NedMembers.TryResolve(new NedMemberKey(a, 1), out _));
    }

    [Fact]
    public void A_stale_member_key_does_not_block_the_member_that_takes_the_index_over()
    {
        var h = Showcase();
        for (int n = 1; n <= 3; n++)
        {
            h.Rpr(S.Platform(n));
        }

        h.Step();
        h.RprStep(S.Aggregate(0x700, "A", 1, 2)); // 1 -> index 0, 2 -> index 1
        h.RprStep(S.Aggregate(0x701, "B", 2)); // 2 is taken over (its key (A,1) is stale until A is flushed again)
        h.Steps(2);
        Assert.True(h.NedIds.TryGetKey(S.RprEntity(h, 0x700).Id, out ulong a));

        h.RprStep(S.Aggregate(0x700, "A", 1, 3)); // 3 takes index 1 of A
        h.Steps(2);

        Assert.True(h.Host.NedMembers.TryResolve(new NedMemberKey(a, 1), out var holder));
        Assert.Equal(S.RprEntity(h, 0x603).Id, holder);
        Assert.Equal(2, h.NedSent<NedFormation>().Last(f => f.Id == a).Members.Length);
    }

    [Fact]
    public void A_linked_member_without_a_position_is_left_out_of_the_formation_until_it_has_one()
    {
        var h = Showcase();
        h.Rpr(S.Platform(1));
        h.Rpr(new RprObjectUpdate(S.PlatformHandle(2))
        {
            Marking = "Bare",
            EntityType = T.Dis(400),
            ForceId = RprForceId.Opposing,
            EntityIdentifier = S.Dis(2),
        });
        h.Step();
        h.RprStep(S.Aggregate(Wolf, "Wolf", 1, 2));
        h.Steps(2);

        var first = h.NedSent<NedFormation>().Last();
        Assert.Equal(new[] { 0 }, first.Members.Select(m => m.Index));

        h.ClearOutput();
        NedRprConversions.GeoToPivot(50.3, 14.3, 100, 0, 0, 0, 0, out var p, out _, out _);
        h.RprStep(new RprObjectUpdate(S.PlatformHandle(2)) { WorldLocation = NedRprConversions.ToWorldLocation(p) });
        h.Steps(2);

        Assert.Equal(new[] { 0, 1 }, Assert.Single(h.NedSent<NedFormation>()).Members.Select(m => m.Index));
    }

    [Fact]
    public void Each_single_attribute_of_a_member_that_a_formation_carries_makes_the_unit_resend_it()
    {
        NedRprConversions.GeoToPivot(50.2, 14.2, 200, 10, 0, 0, 5, out _, out var turned, out _);
        NedRprConversions.GeoToPivot(50.2, 14.2, 200, 270, 0, 0, 15, out _, out _, out var faster);
        var changes = new RprObjectUpdate[]
        {
            new(S.PlatformHandle(2)) { EntityType = T.Dis(100) },
            new(S.PlatformHandle(2)) { Orientation = NedRprConversions.ToRprOrientation(turned) },
            new(S.PlatformHandle(2)) { VelocityVector = NedRprConversions.ToRprVector(faster) },
            new(S.PlatformHandle(2)) { DamageState = RprDamageState.SlightDamage },
        };
        foreach (var change in changes)
        {
            var h = WolfPack(3);

            h.RprStep(change);
            h.Steps(2);

            Assert.Single(h.NedSent<NedFormation>());
        }
    }

    // ---- NED id exhaustion ------------------------------------------------------------------------------------------

    [Fact]
    public void An_exhausted_NED_id_range_warns_once_for_the_formation_and_the_next_change_retries()
    {
        var h = new GatewayHarness(new GatewayConfiguration("tiny", new NedRprProfile(),
            new NedRprSettings { NedIdRange = new NedIdRange(5000, 5000) }));
        h.Checker.Expect("NED id range exhausted");
        h.Rpr(S.Platform(1));
        h.Step(); // platform 1 takes the only NED id as an individual
        h.Rpr(S.Platform(2));
        h.Step(); // platform 2 finds the range exhausted (its own warning)
        Assert.Equal(1, h.Checker.WarningsContaining("entity"));

        h.RprStep(S.Aggregate(Wolf, "Wolf", 2)); // the unit needs an id, too, and gets none
        h.Steps(2);
        h.RprStep(new RprObjectUpdate(S.PlatformHandle(2)) { Marking = "R2b" });
        h.Steps(2);

        Assert.Empty(h.NedSent<NedFormation>());
        Assert.Equal(1, h.Checker.WarningsContaining("formation"));

        h.RprStep(new RprObjectRemoved(S.PlatformHandle(1))); // frees the id
        h.Steps(2);
        h.RprStep(new RprObjectUpdate(S.PlatformHandle(2)) { Marking = "R2c" });
        h.Steps(2);

        var formation = Assert.Single(h.NedSent<NedFormation>());
        Assert.Equal(5000UL, formation.Id);
        Assert.Single(formation.Members);
        Assert.Equal(1, h.Checker.WarningsContaining("formation"));
    }

    // ---- a unit's heartbeat is the unit's, not its members' (HLA-3, R-068) ---------------------------------------------

    [Fact]
    public void A_re_sent_aggregate_writes_the_unit_but_not_the_members_it_already_links()
    {
        var h = WolfPack(3);
        h.Run(2.0);
        var member = S.RprEntity(h, 0x602);
        double unitWrite = S.RprEntity(h, Wolf).LastOriginWriteWallTime;
        double memberWrite = member.LastOriginWriteWallTime;
        Assert.True(member.TryGetSlot(h.Schema.Parent, out var parentBefore));
        Assert.True(member.TryGetSlot(h.Schema.MemberIndex, out var indexBefore));

        h.RprStep(S.Aggregate(Wolf, "Wolf", 1, 2, 3));
        h.Steps(2);

        Assert.True(S.RprEntity(h, Wolf).LastOriginWriteWallTime > unitWrite, "the unit itself was written by its re-sent description");
        var after = S.RprEntity(h, 0x602);
        Assert.Equal(memberWrite, after.LastOriginWriteWallTime); // a member of a live unit still ages out when it is silent (R-068)
        Assert.True(after.TryGetSlot(h.Schema.Parent, out var parentAfter));
        Assert.True(after.TryGetSlot(h.Schema.MemberIndex, out var indexAfter));
        Assert.Equal(parentBefore.Meta.Version, parentAfter.Meta.Version);
        Assert.Equal(indexBefore.Meta.Version, indexAfter.Meta.Version);
    }

    [Fact]
    public void A_re_sent_aggregate_still_links_a_member_that_moved_in_the_list_or_is_new_to_it()
    {
        var h = WolfPack(3, listed: 3);
        h.Rpr(S.Platform(4));
        h.Step();
        h.ClearOutput();

        // member 2 now comes first, member 1 second; member 4 is listed for the first time
        h.RprStep(S.Aggregate(Wolf, "Wolf", 2, 1, 3, 4));
        h.Steps(2);

        var unit = S.RprEntity(h, Wolf);
        var members = S.MembersOf(h, unit);
        Assert.Equal(4, members.Count);
        Assert.Equal(S.RprEntity(h, 0x602).Id, members[0].Id);
        Assert.Equal(S.RprEntity(h, 0x601).Id, members[1].Id);
        Assert.Equal(S.RprEntity(h, 0x604).Id, members[3].Id);
        Assert.Equal(S.MembersByScan(h, unit).Select(m => m.Id), members.Select(m => m.Id));
    }
}
