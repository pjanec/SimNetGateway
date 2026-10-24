using SimNetGateway.Engine;
using SimNetGateway.Protocol.Display;
using SimNetGateway.Protocol.Ned;
using SimNetGateway.Protocol.Rpr;

namespace SimNetGateway.Gateway.NedRpr.Tests;

/// <summary>DD-001 3.7 / DD-002 C3, C9 (R-041, R-042): a NED formation (1) is shattered into one pivot entity per member (N).</summary>
public class FormationTests
{
    private static GatewayHarness Showcase() => new(NedRprConfigurations.Showcase);

    // ---- one formation = one transaction ----------------------------------------------------------------------------

    [Fact]
    public void One_formation_message_is_one_transaction_creating_the_unit_and_all_members_with_parent_and_index()
    {
        var h = Showcase();
        int before = h.Records().Count;

        h.NedStep(S.Viper(0, 1, 2));

        var creating = S.CreatingRecordsSince(h, before);
        var record = Assert.Single(creating);
        Assert.Equal(4, record.Count); // unit + 3 members, one record, four deltas
        Assert.Equal(4, S.CreatedIn(record));
        Assert.Equal(h.NedSource, record.Source);
        var unit = S.NedUnit(h, S.ViperId);
        Assert.Equal("Viper", unit.Get(h.Schema.Name).AsString());
        Assert.False(unit.Has(h.Schema.Position)); // a unit has no position
        Assert.False(unit.Has(h.Schema.Parent));
        var members = S.MembersOf(h, unit);
        Assert.Equal(3, members.Count);
        for (int i = 0; i < 3; i++)
        {
            Assert.Equal(i, members[i].Get(h.Schema.MemberIndex).AsInt());
            Assert.Equal("Viper-" + i, members[i].Get(h.Schema.Name).AsString());
            Assert.Equal(unit.Id, members[i].Get(h.Schema.Parent).AsEntity());
            Assert.Equal(h.NedSource, members[i].Origin);
            Assert.Equal(101, members[i].Get(h.Schema.TypeTkb).AsInt());
            Assert.Equal(1, members[i].Get(h.Schema.Force).AsInt());
            Assert.Same(members[i], S.NedMember(h, S.ViperId, i)); // ned.member (formation, index) resolves the member
        }

        Assert.Equal(3, h.Host.NedMembers.Count);
        Assert.Equal(1, h.NedIds.Count); // only the unit has a ned.entity key
    }

    [Fact]
    public void A_second_formation_message_updates_the_same_entities_in_one_more_transaction()
    {
        var h = Showcase();
        h.NedStep(S.Viper(0, 1, 2));
        var ids = Enumerable.Range(0, 3).Select(i => S.NedMember(h, S.ViperId, i).Id).ToArray();
        int before = h.Records().Count;

        h.NedStep(S.Formation(S.ViperId, "Viper", S.Member(0, lat: 50.001), S.Member(1, lat: 50.001), S.Member(2, lat: 50.001)));

        Assert.Empty(S.CreatingRecordsSince(h, before)); // nothing new was created
        Assert.Equal(ids, Enumerable.Range(0, 3).Select(i => S.NedMember(h, S.ViperId, i).Id));
        Assert.Equal(4, h.Engine.State.Count);
    }

    [Fact]
    public void A_bad_member_aborts_the_whole_formation_and_leaves_the_repository_unchanged()
    {
        var h = Showcase();
        h.NedStep(S.Viper(0, 1));
        string snapshot = h.Snapshot();
        int records = S.RecordsFrom(h, h.NedSource);
        h.Checker.Expect("damage level 9");
        h.Checker.Expect("listed twice");
        h.Checker.Expect("is negative");

        h.NedStep(S.Formation(S.ViperId, "Viper-X", S.Member(0), S.Member(1, damage: 9)));
        h.NedStep(S.Formation(S.ViperId, "Viper-X", S.Member(0), S.Member(0)));
        h.NedStep(S.Formation(S.ViperId, "Viper-X", S.Member(-1)));

        Assert.Equal(snapshot, h.Snapshot());
        Assert.Equal(records, S.RecordsFrom(h, h.NedSource)); // an aborted transaction leaves no journal record
        Assert.Equal(3, h.Engine.Diagnostics.TransactionFailures);
        Assert.Equal("Viper", S.NedUnit(h, S.ViperId).Get(h.Schema.Name).AsString());
    }

    [Fact]
    public void A_formation_that_would_create_new_entities_but_aborts_creates_nothing_and_binds_no_key()
    {
        var h = Showcase();
        h.Checker.Expect("damage level");

        h.NedStep(S.Formation(9, "Bad", S.Member(0), S.Member(1), S.Member(2, damage: 4)));

        Assert.Equal(0, h.Engine.State.Count);
        Assert.Equal(0, h.NedIds.Count);
        Assert.Equal(0, h.Host.NedMembers.Count);
    }

    // ---- towards RPR: one platform per member ------------------------------------------------------------------------

    [Fact]
    public void RPR_gets_one_platform_per_member_with_distinct_handles_and_DIS_ids_and_none_for_the_unit()
    {
        var h = Showcase();

        h.NedStep(S.Viper(0, 1, 2));
        h.Steps(2);

        Assert.Equal(3, h.RprSent<RprRegisterObject>().Count); // the unit has no position: not registered
        var updates = h.RprSent<RprObjectUpdate>();
        Assert.Equal(3, updates.Count);
        Assert.Equal(3, updates.Select(u => u.Handle).Distinct().Count());
        Assert.Equal(3, updates.Select(u => u.EntityIdentifier).Distinct().Count());
        Assert.Equal(new[] { "Viper-0", "Viper-1", "Viper-2" }, updates.Select(u => u.Marking!).OrderBy(x => x));
        Assert.All(updates, u => Assert.Equal(RprForceId.Friendly, u.ForceId));
        Assert.All(updates, u => Assert.Equal(T.Dis(101), u.EntityType));
        var unit = S.NedUnit(h, S.ViperId);
        Assert.False(h.RprHandles.Contains(unit.Id));
        Assert.False(h.RprEntityIds.Contains(unit.Id));
        Assert.Equal(3, h.RprHandles.Count);
        Assert.Empty(h.NedOut.Messages); // nothing goes back to NED
    }

    [Fact]
    public void Member_updates_while_the_handles_are_pending_are_coalesced_into_the_first_full_update()
    {
        var h = new GatewayHarness(NedRprConfigurations.Showcase) { DeliverRti = false };
        h.NedStep(S.Viper(0, 1, 2));
        var moved = S.Formation(S.ViperId, "Viper", S.Member(0, lat: 50.01, speed: 30), S.Member(1, lat: 50.01, speed: 30),
            S.Member(2, lat: 50.01, speed: 30, damage: 2));
        h.NedStep(moved);
        h.NedStep(moved with { Members = moved.Members.Select(m => m with { Altitude = 350 }).ToArray() });
        h.Run(0.5);
        Assert.Equal(3, h.RprOut.Count); // only the three registration requests: nothing was sent with a null handle

        h.DeliverRti = true;
        h.Steps(2);

        var updates = h.RprSent<RprObjectUpdate>();
        Assert.Equal(3, updates.Count); // one per member, however many formations arrived meanwhile
        var last = moved.Members[2];
        var third = updates.Single(u => u.Marking == "Viper-2");
        NedRprConversions.GeoToPivot(last.LatitudeDeg, last.LongitudeDeg, 350, last.HeadingDeg, last.PitchDeg, last.RollDeg, last.Speed,
            out var position, out _, out _);
        Assert.Equal(NedRprConversions.ToWorldLocation(position), third.WorldLocation);
        Assert.Equal(RprDamageState.ModerateDamage, third.DamageState);
        Assert.Equal(3, h.RprSent<RprRegisterObject>().Count);
    }

    [Fact]
    public void Member_identity_is_stable_over_many_updates()
    {
        var h = Showcase();
        h.NedStep(S.Viper(0, 1, 2));
        h.Steps(2);
        var entities = Enumerable.Range(0, 3).Select(i => S.NedMember(h, S.ViperId, i).Id).ToArray();
        var handles = entities.Select(e => h.RprHandles.TryGetKey(e, out var k) ? k : default).ToArray();
        var numbers = entities.Select(e => h.RprEntityIds.TryGetKey(e, out var k) ? k : default).ToArray();

        for (int step = 1; step <= 30; step++)
        {
            h.NedStep(S.Formation(S.ViperId, "Viper", Enumerable.Range(0, 3).Select(i => S.Member(i, lat: 50.0 + step * 1e-4)).ToArray()));
        }

        Assert.Equal(4, h.Engine.State.Count);
        Assert.Equal(entities, Enumerable.Range(0, 3).Select(i => S.NedMember(h, S.ViperId, i).Id));
        Assert.Equal(handles, entities.Select(e => h.RprHandles.TryGetKey(e, out var k) ? k : default));
        Assert.Equal(numbers, entities.Select(e => h.RprEntityIds.TryGetKey(e, out var k) ? k : default));
        Assert.Equal(3, h.RprSent<RprRegisterObject>().Count);
        Assert.Equal(3 + 30 * 3, h.RprSent<RprObjectUpdate>().Count);
    }

    [Fact]
    public void The_display_gets_a_marker_per_member_labelled_unit_dash_index()
    {
        var h = Showcase();
        h.NedStep(S.Viper(0, 1, 2));
        h.NedStep(T.Master(44, "Gate", 300));
        h.NedStep(T.Geo(44, speed: 0));

        h.Run(0.5);

        var labels = h.DisplaySent<DisplayEntityPosition>().Select(p => p.Label).Distinct().OrderBy(x => x).ToList();
        Assert.Equal(new[] { "Gate", "Viper-0", "Viper-1", "Viper-2" }, labels);
        Assert.True(h.DisplayLabels.TryResolve("Viper-1", out var member));
        Assert.Same(S.NedMember(h, S.ViperId, 1), h.Engine.State.Find(member));
    }

    // ---- members that disappear ---------------------------------------------------------------------------------------

    [Fact]
    public void A_member_dropped_from_the_next_formation_is_destroyed_removed_from_RPR_and_display_and_its_key_released()
    {
        var h = Showcase();
        h.NedStep(S.Viper(0, 1, 2));
        h.Run(0.5);
        var dropped = S.NedMember(h, S.ViperId, 1);
        var droppedId = dropped.Id;
        var handle = h.HandleOf(dropped);
        h.ClearOutput();

        h.NedStep(S.Viper(0, 2));
        h.Run(0.3);

        Assert.Null(h.Engine.State.Find(droppedId));
        Assert.False(dropped.IsAlive);
        Assert.False(h.Host.NedMembers.TryResolve(new NedMemberKey(S.ViperId, 1), out _)); // the ned.member key is released
        Assert.Equal(2, h.Host.NedMembers.Count);
        Assert.Equal(handle, Assert.Single(h.RprSent<RprObjectRemoved>()).Handle);
        Assert.Equal("Viper-1", Assert.Single(h.DisplaySent<DisplayEntityRemoved>()).Label);
        Assert.False(h.DisplayLabels.TryResolve("Viper-1", out _));
        Assert.Equal(2, S.MembersOf(h, S.NedUnit(h, S.ViperId)).Count);
    }

    [Fact]
    public void A_dropped_index_can_come_back_as_a_new_entity()
    {
        var h = Showcase();
        h.NedStep(S.Viper(0, 1, 2));
        var first = S.NedMember(h, S.ViperId, 1).Id;
        h.NedStep(S.Viper(0, 2));

        h.NedStep(S.Viper(0, 1, 2));

        var again = S.NedMember(h, S.ViperId, 1);
        Assert.NotEqual(first, again.Id);
        Assert.Equal(3, S.MembersOf(h, S.NedUnit(h, S.ViperId)).Count);
    }

    [Fact]
    public void An_empty_member_list_removes_all_members_but_keeps_the_unit()
    {
        var h = Showcase();
        h.NedStep(S.Viper(0, 1, 2));
        h.Steps(2);
        h.ClearOutput();

        h.NedStep(S.Formation(S.ViperId, "Viper"));
        h.Steps(2);

        Assert.Equal(1, h.Engine.State.Count);
        Assert.Equal(3, h.RprSent<RprObjectRemoved>().Count);
        Assert.Empty(S.MembersOf(h, S.NedUnit(h, S.ViperId)));
    }

    [Fact]
    public void A_disposed_formation_takes_its_members_with_it()
    {
        var h = Showcase();
        h.NedStep(S.Viper(0, 1, 2));
        h.Steps(2);
        h.ClearOutput();

        h.NedStep(new NedEntityDisposed(S.ViperId));
        h.Steps(2);

        Assert.Equal(0, h.Engine.State.Count);
        Assert.Equal(3, h.RprSent<RprObjectRemoved>().Count);
        Assert.Equal(0, h.Host.NedMembers.Count);
        Assert.Equal(0, h.NedIds.Count);
    }

    [Fact]
    public void Members_of_two_formations_do_not_interfere()
    {
        var h = Showcase();
        h.NedStep(S.Formation(7, "Viper", S.Member(0), S.Member(1)));
        h.NedStep(S.Formation(8, "Cobra", S.Member(0), S.Member(1), S.Member(2)));

        h.NedStep(S.Formation(7, "Viper", S.Member(1)));

        Assert.Single(S.MembersOf(h, S.NedUnit(h, 7)));
        Assert.Equal(3, S.MembersOf(h, S.NedUnit(h, 8)).Count);
        Assert.NotEqual(S.NedMember(h, 7, 1).Id, S.NedMember(h, 8, 1).Id);
        Assert.Equal(2 + 4, h.Engine.State.Count);
    }

    // ---- looped back publications ------------------------------------------------------------------------------------

    [Fact]
    public void A_looped_back_formation_of_an_RPR_origin_unit_is_ignored_whole()
    {
        var h = Showcase();
        h.LoopbackNed = true;
        h.Rpr(S.Platform(1));
        h.Rpr(S.Platform(2));
        h.Step();
        h.RprStep(S.Aggregate(0x700, "Wolf", 1, 2));
        h.Run(0.5); // the NedFormation the gateway sent came back and was ignored
        string snapshot = h.Snapshot();
        var formations = h.NedSent<NedFormation>();
        Assert.Single(formations);
        int sent = h.NedOut.Count;

        h.Run(0.5);

        Assert.Equal(snapshot, h.Snapshot());
        Assert.Equal(sent, h.NedOut.Count);
        Assert.Equal(0, h.Engine.Diagnostics.TransactionFailures);
        Assert.Equal(3, h.Engine.State.Count); // unit + two members, no entity from the looped-back message
    }

    [Fact]
    public void A_formation_naming_an_unknown_force_is_rejected()
    {
        var h = Showcase();
        h.Checker.Expect("force 9");

        h.NedStep(new NedFormation(7, "Viper", 100, (NedForce)9, [S.Member(0)]));

        Assert.Equal(0, h.Engine.State.Count);
    }

    // ---- events with member targets (C9) ---------------------------------------------------------------------------

    [Fact]
    public void A_NED_fire_naming_formation_and_member_reaches_RPR_with_the_member_platform_as_target()
    {
        var h = Showcase();
        h.NedStep(S.Viper(0, 1, 2));
        h.NedStep(T.Master(T.Hawk, "Hawk", 200));
        h.NedStep(T.Geo(T.Hawk));
        h.NedStep(new NedWeaponStatus(T.Hawk, 501, 10));
        h.Steps(3);
        var member = S.NedMember(h, S.ViperId, 1);
        h.ClearOutput();

        var fireId = Guid.NewGuid();
        h.NedStep(new NedWeaponFire(fireId, T.Hawk, S.ViperId, 0, 50, 14, 300, 1, TargetMember: 1));
        h.NedStep(new NedFireAmmunition(fireId, 501, 1));
        h.Steps(2);

        var fire = Assert.Single(h.RprSent<RprWeaponFire>());
        Assert.Equal(h.HandleOf(member), fire.TargetObject);
        Assert.Equal(h.HandleOf(h.EntityOfNed(T.Hawk)!), fire.FiringObject);
    }

    [Fact]
    public void A_NED_detonation_with_a_target_member_resolves_the_member_and_sets_its_damage()
    {
        var h = Showcase();
        h.NedStep(S.Viper(0, 1, 2));
        h.Steps(3);
        var member = S.NedMember(h, S.ViperId, 1);
        var other = S.NedMember(h, S.ViperId, 0);
        h.ClearOutput();

        h.NedStep(new NedDetonation(Guid.NewGuid(), S.ViperId, 501, 50, 14, 300, NedDetonationResult.EntityImpact, 3, TargetMember: 1));
        h.Steps(2);

        Assert.Equal(3, member.Get(h.Schema.Damage).AsInt());
        Assert.Equal(0, other.Get(h.Schema.Damage).AsInt());
        var detonation = Assert.Single(h.RprSent<RprMunitionDetonation>());
        Assert.Equal(h.HandleOf(member), detonation.TargetObject);
        var update = Assert.Single(h.RprSent<RprObjectUpdate>());
        Assert.Equal(h.HandleOf(member), update.Handle);
        Assert.Equal(RprDamageState.Destroyed, update.DamageState);
    }

    [Fact]
    public void A_detonation_naming_an_unknown_member_has_no_target_and_changes_nothing()
    {
        var h = Showcase();
        h.NedStep(S.Viper(0, 1));
        h.Steps(3);
        string snapshot = h.Snapshot();
        h.ClearOutput();

        h.NedStep(new NedDetonation(Guid.NewGuid(), S.ViperId, 501, 50, 14, 300, NedDetonationResult.EntityImpact, 3, TargetMember: 5));
        h.Steps(2);

        Assert.Equal(snapshot, h.Snapshot()); // no damage anywhere (not even on the unit)
        Assert.Null(Assert.Single(h.RprSent<RprMunitionDetonation>()).TargetObject);
    }

    [Fact]
    public void A_target_member_index_is_not_confused_with_the_entity_id_of_the_unit()
    {
        var h = Showcase();
        h.NedStep(S.Viper(0, 1));
        h.NedStep(T.Master(T.Hawk, "Hawk", 200));
        h.NedStep(T.Geo(T.Hawk));
        h.Steps(3);
        h.ClearOutput();

        // member -1 = the unit itself: the unit has no RPR handle, so "no object"
        h.NedStep(new NedDetonation(Guid.NewGuid(), S.ViperId, 501, 50, 14, 300, NedDetonationResult.EntityImpact, 2));
        h.Steps(2);

        Assert.Null(Assert.Single(h.RprSent<RprMunitionDetonation>()).TargetObject);
        Assert.Equal(2, S.NedUnit(h, S.ViperId).Get(h.Schema.Damage).AsInt());
        Assert.Equal(0, S.NedMember(h, S.ViperId, 1).Get(h.Schema.Damage).AsInt());
    }
}
