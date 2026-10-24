using SimNetGateway.Engine;

namespace SimNetGateway.Engine.Tests;

/// <summary>A coarse NED formation descriptor: one entity with <c>Members</c> parts.</summary>
internal readonly record struct FormationMsg(ulong Id, int Members, double X);

/// <summary>A fine-side part that names the unit it will belong to.</summary>
internal readonly record struct PartMsg(ulong Part, ulong Unit);

/// <summary>A fine-side unit descriptor.</summary>
internal readonly record struct UnitMsg(ulong Unit);

/// <summary>DD-001 3.7 / 11.9: one coarse entity to many fine entities and back (engine part).</summary>
public class CardinalityTests
{
    private sealed class World
    {
        public readonly EngineFixture F = new();
        public readonly AttrId Parent;
        public readonly AttrId MemberIndex;
        public readonly AttrId UnitKey;
        public readonly AttrId PendingUnit;
        public readonly AttrId EventTarget;
        public IdentityMap<ulong> Units = null!;
        public IdentityMap<MemberKey> Members = null!;
        public IdentityMap<ulong> Parts = null!;

        // the aggregating egress
        public readonly HashSet<EntityId> Dirty = new();
        public readonly List<(EntityId Parent, int[] Members)> Sent = new();
        public readonly List<int> ChangeSetSizes = new();

        public bool Conflict;

        public World()
        {
            var a = F.Engine.Attributes;
            Parent = a.Register("Structure.Parent", AttrKind.EntityRef, null, AttrOptions.IndexReferences);
            MemberIndex = a.Register("Structure.MemberIndex", AttrKind.Int);
            UnitKey = a.Register("Unit.Key", AttrKind.Int);
            PendingUnit = a.Register("Member.PendingUnit", AttrKind.Int);
            EventTarget = a.Register("Event.Target", AttrKind.EntityRef);

            F.Module("ned.in", s =>
            {
                Units = s.Identities.Create<ulong>(F.Net, "ned.entity");
                Members = s.Identities.Create<MemberKey>(F.Net, "ned.member");
                var scratch = new List<EntityRecord>();
                s.OnMessage<FormationMsg>((in FormationMsg m, IngressContext ctx) =>
                {
                    var cs = ctx.Changes;
                    if (!Units.TryResolve(cs, m.Id, out var unit))
                    {
                        unit = cs.CreateEntity();
                        Units.Bind(cs, unit, m.Id);
                        cs.Set(unit, UnitKey, EngineFixture.I((long)m.Id));
                    }

                    for (int i = 0; i < m.Members; i++)
                    {
                        var key = new MemberKey(m.Id, i);
                        if (!Members.TryResolve(cs, key, out var member))
                        {
                            member = cs.CreateEntity();
                            Members.Bind(cs, member, key);
                        }

                        cs.Set(member, Parent, AttrValue.From(unit));
                        cs.Set(member, MemberIndex, EngineFixture.I(i));
                        cs.Set(member, F.Pos, EngineFixture.V(m.X + i));
                    }

                    // parts that disappeared from the message
                    ctx.State.Query().ReferencedBy(Parent, unit).CopyTo(scratch);
                    foreach (var e in scratch)
                    {
                        if (e.Get(MemberIndex).AsInt() >= m.Members)
                        {
                            cs.DestroyEntity(e.Id);
                        }
                    }
                });
                s.OnMessage<FormationMsg>((in FormationMsg m, IngressContext ctx) =>
                {
                    if (Conflict)
                    {
                        // a second handler that binds an existing composite key to another entity
                        Members.Bind(ctx.Changes, ctx.Changes.CreateEntity(), new MemberKey(m.Id, 0));
                    }
                });
            });
        }

        /// <summary>The aggregating egress: one coarse message per dirty parent, at the end of the change set.</summary>
        public void AddAggregatingEgress() => F.Module("ned.out", s =>
        {
            s.OnAttributesChanged(F.Pos).Do(t =>
            {
                if (t.Entity.TryGet(Parent, out var parent))
                {
                    Dirty.Add(parent.AsEntity());
                }
            });
            s.OnChangeSetEnd().Do(t =>
            {
                ChangeSetSizes.Add(t.ChangeSet.Count);
                var members = new List<EntityRecord>();
                foreach (var parent in Dirty.OrderBy(p => p.Value))
                {
                    F.Engine.State.Query().ReferencedBy(Parent, parent).CopyTo(members);
                    Sent.Add((parent, members.Select(m => m.Id.Value).Order().ToArray()));
                }

                Dirty.Clear();
            });
        });

        public int[] MembersOf(EntityId unit) => QueryFixture.Ids(F.Engine.State.Query().ReferencedBy(Parent, unit));

        public EntityId Unit(ulong key)
        {
            Assert.True(Units.TryResolve(key, out var id));
            return id;
        }

        public EntityId Member(ulong unit, int index)
        {
            Assert.True(Members.TryResolve(new MemberKey(unit, index), out var id));
            return id;
        }
    }

    [Fact]
    public void One_coarse_message_creates_the_unit_and_its_parts_in_one_transaction()
    {
        var w = new World();
        w.AddAggregatingEgress();

        w.F.Engine.Ingest(w.F.Net, new FormationMsg(7, 3, 100));

        Assert.Equal(4, w.F.Engine.State.Count);
        var unit = w.Unit(7);
        Assert.Equal(3, w.F.Engine.State.Query().ReferencedBy(w.Parent, unit).Count());
        Assert.Equal(new[] { 4 }, w.ChangeSetSizes.ToArray());        // one change set holding unit + 3 parts
        Assert.Equal(1, w.F.Engine.State.Journal.Count);
        for (int i = 0; i < 3; i++)
        {
            var member = w.F.Engine.State.Find(w.Member(7, i))!;
            Assert.Equal(unit, member.Get(w.Parent).AsEntity());
            Assert.Equal(i, member.Get(w.MemberIndex).AsInt());
        }

        Assert.Equal(1, w.Units.Count);
        Assert.Equal(3, w.Members.Count);
    }

    [Fact]
    public void A_part_keeps_its_identity_across_updates()
    {
        var w = new World();
        w.F.Engine.Ingest(w.F.Net, new FormationMsg(7, 3, 100));
        var before = new[] { w.Member(7, 0), w.Member(7, 1), w.Member(7, 2) };
        var unit = w.Unit(7);

        w.F.Engine.Ingest(w.F.Net, new FormationMsg(7, 3, 500));

        Assert.Equal(4, w.F.Engine.State.Count);
        Assert.Equal(before, new[] { w.Member(7, 0), w.Member(7, 1), w.Member(7, 2) });
        Assert.Equal(unit, w.Unit(7));
        Assert.Equal(501, w.F.Engine.State.Find(before[1])!.Get(w.F.Pos).AsVec3().X);
        Assert.Equal(3, w.MembersOf(unit).Length);
    }

    [Fact]
    public void A_part_that_disappears_from_the_message_is_removed_and_its_key_released()
    {
        var w = new World();
        w.F.Engine.Ingest(w.F.Net, new FormationMsg(7, 3, 0));
        var unit = w.Unit(7);
        var third = w.Member(7, 2);

        w.F.Engine.Ingest(w.F.Net, new FormationMsg(7, 2, 0));

        Assert.Equal(3, w.F.Engine.State.Count);
        Assert.Null(w.F.Engine.State.Find(third));
        Assert.Equal(2, w.MembersOf(unit).Length);
        Assert.False(w.Members.TryResolve(new MemberKey(7, 2), out _));

        // the part comes back: a new entity takes the released key
        w.F.Engine.Ingest(w.F.Net, new FormationMsg(7, 3, 0));
        Assert.Equal(4, w.F.Engine.State.Count);
        Assert.NotEqual(third, w.Member(7, 2));
        Assert.Equal(3, w.MembersOf(unit).Length);
    }

    [Fact]
    public void A_failing_coarse_message_changes_nothing_all_or_nothing()
    {
        var w = new World();
        w.F.Engine.Ingest(w.F.Net, new FormationMsg(7, 2, 0));
        var unit = w.Unit(7);
        long rejectedBefore = w.F.Engine.Diagnostics.TransactionFailures;

        w.Conflict = true; // the second handler binds MemberKey(7, 0) to another entity: the commit is rejected
        w.F.Engine.Ingest(w.F.Net, new FormationMsg(7, 3, 900));
        w.F.Engine.Ingest(w.F.Net, new FormationMsg(8, 2, 900)); // a brand-new formation fails as a whole too

        Assert.Equal(rejectedBefore + 2, w.F.Engine.Diagnostics.TransactionFailures);
        Assert.Equal(3, w.F.Engine.State.Count);
        Assert.Equal(2, w.MembersOf(unit).Length);
        Assert.False(w.Units.TryResolve(8, out _));
        Assert.False(w.Members.TryResolve(new MemberKey(7, 2), out _));
        Assert.Equal(0, w.F.Engine.State.Find(w.Member(7, 0))!.Get(w.F.Pos).AsVec3().X); // the Pos of 900 never arrived
    }

    [Fact]
    public void Reparenting_moves_the_part_in_the_reverse_index()
    {
        var w = new World();
        w.F.Engine.Ingest(w.F.Net, new FormationMsg(1, 2, 0));
        w.F.Engine.Ingest(w.F.Net, new FormationMsg(2, 1, 0));
        var a = w.Unit(1);
        var b = w.Unit(2);
        var moving = w.Member(1, 1);
        Assert.Equal(2, w.MembersOf(a).Length);
        Assert.Single(w.MembersOf(b));

        Assert.True(w.F.Write(moving, w.Parent, AttrValue.From(b)).Succeeded);

        Assert.Single(w.MembersOf(a));
        Assert.Equal(2, w.MembersOf(b).Length);
        Assert.Contains(moving.Value, w.MembersOf(b));
        Assert.DoesNotContain(moving.Value, w.MembersOf(a));
    }

    [Fact]
    public void Destroying_a_part_removes_it_from_the_reverse_index()
    {
        var w = new World();
        w.F.Engine.Ingest(w.F.Net, new FormationMsg(1, 3, 0));
        var unit = w.Unit(1);
        var part = w.Member(1, 0);

        var cs = w.F.Engine.BeginChange(w.F.Net);
        cs.DestroyEntity(part);
        Assert.True(cs.Commit().Succeeded);

        Assert.Equal(2, w.MembersOf(unit).Length);
        Assert.DoesNotContain(part.Value, w.MembersOf(unit));
    }

    [Fact]
    public void A_removal_rule_with_the_reverse_index_removes_the_parts_of_a_destroyed_parent()
    {
        var w = new World();
        w.F.Module("structure", s =>
        {
            var parts = new List<EntityRecord>();
            s.OnEntityRemoved().When(t => t.Entity.Has(w.UnitKey)).Do(t =>
            {
                // the destroyed unit's referrers are still listed: they hold the reference
                w.F.Engine.State.Query().ReferencedBy(w.Parent, t.Id).CopyTo(parts);
                var cs = w.F.Engine.BeginChange(w.F.Proc);
                foreach (var part in parts)
                {
                    cs.DestroyEntity(part.Id);
                }

                cs.Commit();
            });
        });
        w.F.Engine.Ingest(w.F.Net, new FormationMsg(1, 3, 0));
        var unit = w.Unit(1);
        Assert.Equal(4, w.F.Engine.State.Count);

        var cs = w.F.Engine.BeginChange(w.F.Net);
        cs.DestroyEntity(unit);
        Assert.True(cs.Commit().Succeeded);

        Assert.Equal(0, w.F.Engine.State.Count);
        Assert.Equal(0, w.Members.Count);
        Assert.Equal(0, w.Units.Count);
        Assert.Equal(0, w.F.Engine.State.Query().ReferencedBy(w.Parent, unit).Count());
    }

    [Fact]
    public void ChangeSetEnd_with_the_reverse_index_lets_an_egress_send_one_message_per_dirty_parent()
    {
        var w = new World();
        w.AddAggregatingEgress();
        w.F.Engine.Ingest(w.F.Net, new FormationMsg(1, 3, 0));
        w.F.Engine.Ingest(w.F.Net, new FormationMsg(2, 2, 0));
        w.Sent.Clear();
        var u1 = w.Unit(1);
        var u2 = w.Unit(2);

        // all three parts of unit 1 change in one message: one aggregate message, not three
        w.F.Engine.Ingest(w.F.Net, new FormationMsg(1, 3, 50));
        Assert.Single(w.Sent);
        Assert.Equal(u1, w.Sent[0].Parent);
        Assert.Equal(3, w.Sent[0].Members.Length);

        // one part of unit 2 changes in its own change set: one message for unit 2
        w.Sent.Clear();
        Assert.True(w.F.Write(w.Member(2, 1), w.F.Pos, EngineFixture.V(999)).Succeeded);
        Assert.Single(w.Sent);
        Assert.Equal(u2, w.Sent[0].Parent);
        Assert.Equal(2, w.Sent[0].Members.Length);

        // parts of two parents in one change set: one message each, in one change-set-end
        w.Sent.Clear();
        var cs = w.F.Engine.BeginChange(w.F.Net);
        cs.Set(w.Member(1, 0), w.F.Pos, EngineFixture.V(1234));
        cs.Set(w.Member(1, 2), w.F.Pos, EngineFixture.V(1234));
        cs.Set(w.Member(2, 0), w.F.Pos, EngineFixture.V(1234));
        Assert.True(cs.Commit().Succeeded);
        Assert.Equal(2, w.Sent.Count);
        Assert.Equal(new[] { u1, u2 }, w.Sent.Select(s => s.Parent).ToArray());

        // a change that touches no part sends nothing
        w.Sent.Clear();
        Assert.True(w.F.Write(u1, w.F.Count, EngineFixture.I(1)).Succeeded);
        Assert.Empty(w.Sent);
    }

    [Fact]
    public void Parts_that_arrive_before_their_parent_are_linked_when_the_parent_arrives()
    {
        var w = new World();
        var parts = w.F.Engine.Identities.Create<ulong>(w.F.Net, "rpr.part");
        w.F.Module("fine.in", s =>
        {
            s.OnMessage<PartMsg>((in PartMsg m, IngressContext ctx) =>
            {
                var part = ctx.Changes.CreateEntity();
                parts.Bind(ctx.Changes, part, m.Part);
                ctx.Changes.Set(part, w.PendingUnit, EngineFixture.I((long)m.Unit));
            });
            s.OnMessage<UnitMsg>((in UnitMsg m, IngressContext ctx) =>
            {
                var unit = ctx.Changes.CreateEntity();
                w.Units.Bind(ctx.Changes, unit, m.Unit);
                ctx.Changes.Set(unit, w.UnitKey, EngineFixture.I((long)m.Unit));
            });
        });
        w.F.Module("linker", s =>
        {
            var waiting = new List<EntityRecord>();
            s.OnEntityCreated().When(t => t.Entity.Has(w.UnitKey)).Do(t =>
            {
                long key = t.Entity.Get(w.UnitKey).AsInt();
                w.F.Engine.State.Query().Has(w.PendingUnit).Lacks(w.Parent)
                    .Where((Attr: w.PendingUnit, Key: key), static (e, state) => e.Get(state.Attr).AsInt() == state.Key)
                    .CopyTo(waiting);
                var cs = w.F.Engine.BeginChange(w.F.Proc);
                foreach (var part in waiting)
                {
                    cs.Set(part.Id, w.Parent, AttrValue.From(t.Id));
                }

                cs.Commit();
            });
        });

        w.F.Engine.Ingest(w.F.Net, new PartMsg(101, 9));
        w.F.Engine.Ingest(w.F.Net, new PartMsg(102, 9));
        w.F.Engine.Ingest(w.F.Net, new PartMsg(103, 8));
        Assert.Equal(3, w.F.Engine.State.Query().Has(w.PendingUnit).Lacks(w.Parent).Count()); // no parent yet

        w.F.Engine.Ingest(w.F.Net, new UnitMsg(9));

        var unit = w.Unit(9);
        Assert.Equal(2, w.MembersOf(unit).Length);
        Assert.Equal(1, w.F.Engine.State.Query().Has(w.PendingUnit).Lacks(w.Parent).Count()); // part 103 still waits for unit 8
        Assert.True(parts.TryResolve(101, out var p101));
        Assert.Contains(p101.Value, w.MembersOf(unit));
    }

    [Fact]
    public void An_event_on_a_part_is_mapped_to_the_key_of_the_coarse_parent()
    {
        var w = new World();
        w.F.Engine.Ingest(w.F.Net, new FormationMsg(7, 3, 0));
        ulong coarseKey = 0;
        int partIndex = -1;
        w.F.Module("ned.out", s => s.OnEvent(w.F.Fired).Do(t =>
        {
            var part = w.F.Engine.State.Find(t.Payload.Get(w.EventTarget).AsEntity())!;
            var parent = part.Get(w.Parent).AsEntity();                   // E12 -> Structure.Parent -> E10
            Assert.True(w.Units.TryGetKey(parent, out coarseKey));        // E10 -> 7
            partIndex = (int)part.Get(w.MemberIndex).AsInt();
        }));

        var payload = w.F.Engine.RentPayload().Set(w.EventTarget, AttrValue.From(w.Member(7, 1)));
        w.F.Engine.Publish(w.F.Net, w.F.Fired, payload);

        Assert.Equal(7UL, coarseKey);
        Assert.Equal(1, partIndex);
    }
}
