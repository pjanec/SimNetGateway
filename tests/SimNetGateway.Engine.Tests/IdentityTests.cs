using SimNetGateway.Engine;

namespace SimNetGateway.Engine.Tests;

/// <summary>An RTI-style object handle: a struct wire key.</summary>
internal readonly record struct RprHandle(uint Value);

/// <summary>A (site, application, entity number) wire key.</summary>
internal readonly record struct DisId(ushort Site, ushort App, ushort Number);

/// <summary>A composite key: member <c>Index</c> of formation <c>Formation</c>.</summary>
internal readonly record struct MemberKey(ulong Formation, int Index);

/// <summary>Hands out 1, 2, 3 ... up to a limit and records what was released (no reuse).</summary>
internal sealed class CountingAllocator : IIdAllocator<ulong>
{
    private readonly ulong _limit;
    private ulong _next = 1000;

    public CountingAllocator(int capacity) => _limit = _next + (ulong)capacity;

    public List<ulong> Released { get; } = new();

    public int Allocated { get; private set; }

    public bool TryAllocate(out ulong key)
    {
        if (_next >= _limit)
        {
            key = 0;
            return false;
        }

        key = _next++;
        Allocated++;
        return true;
    }

    public void Release(in ulong key) => Released.Add(key);
}

/// <summary>
/// A pool of entity numbers under a fixed site/application; a released number is reused only after a quarantine
/// (measured on a manual clock).
/// </summary>
internal sealed class QuarantineAllocator : IIdAllocator<DisId>
{
    private readonly Func<double> _now;
    private readonly double _quarantine;
    private readonly Queue<(ushort Number, double FreeAt)> _freed = new();
    private ushort _next = 1;
    private readonly ushort _max;

    public QuarantineAllocator(Func<double> now, double quarantine, ushort max)
    {
        _now = now;
        _quarantine = quarantine;
        _max = max;
    }

    public bool TryAllocate(out DisId key)
    {
        if (_freed.Count > 0 && _freed.Peek().FreeAt <= _now())
        {
            key = new DisId(1, 3001, _freed.Dequeue().Number);
            return true;
        }

        if (_next <= _max)
        {
            key = new DisId(1, 3001, _next++);
            return true;
        }

        key = default;
        return false;
    }

    public void Release(in DisId key) => _freed.Enqueue((key.Number, _now() + _quarantine));
}

/// <summary>A message that introduces an entity under a NED key.</summary>
internal readonly record struct KeyMsg(ulong Key);

public class IdentityTests
{
    private static IdentityMap<ulong> NedMap(EngineFixture f, IIdAllocator<ulong>? allocator = null) =>
        f.Engine.Identities.Create<ulong>(f.Net, "ned.entity", allocator);

    [Fact]
    public void Four_key_types_in_four_maps_resolve_the_same_entity()
    {
        var f = new EngineFixture();
        var ned = NedMap(f);
        var handle = f.Engine.Identities.Create<RprHandle>(f.Proc, "rpr.handle");
        var dis = f.Engine.Identities.Create<DisId>(f.Proc, "rpr.entityId");
        var label = f.Engine.Identities.Create<string>(f.Proc, "display.label");
        f.Module("ingress", s => s.OnMessage<KeyMsg>((in KeyMsg m, IngressContext ctx) =>
        {
            var id = ctx.Changes.CreateEntity();
            ned.Bind(ctx.Changes, id, m.Key);
        }));

        f.Engine.Ingest(f.Net, new KeyMsg(42));

        Assert.True(ned.TryResolve(42, out var id));
        Assert.True(handle.Bind(id, new RprHandle(0x2A01)));
        Assert.True(dis.Bind(id, new DisId(1, 3001, 17)));
        Assert.True(label.Bind(id, "NED-42"));

        Assert.True(handle.TryResolve(new RprHandle(0x2A01), out var viaHandle));
        Assert.True(dis.TryResolve(new DisId(1, 3001, 17), out var viaDis));
        Assert.True(label.TryResolve("NED-42", out var viaLabel));
        Assert.Equal(id, viaHandle);
        Assert.Equal(id, viaDis);
        Assert.Equal(id, viaLabel);

        Assert.True(ned.TryGetKey(id, out var nedKey));
        Assert.True(handle.TryGetKey(id, out var h));
        Assert.True(dis.TryGetKey(id, out var d));
        Assert.True(label.TryGetKey(id, out var l));
        Assert.Equal(42UL, nedKey);
        Assert.Equal(new RprHandle(0x2A01), h);
        Assert.Equal(new DisId(1, 3001, 17), d);
        Assert.Equal("NED-42", l);

        Assert.Equal(typeof(DisId), dis.KeyType);
        Assert.Equal(f.Proc, dis.Side);
        Assert.Equal("rpr.entityId", dis.Name);
        Assert.True(ned.Contains(id));
        Assert.False(ned.TryResolve(43, out _));
    }

    [Fact]
    public void Unbind_removes_the_binding_of_a_live_entity_and_returns_an_allocated_key_to_its_allocator()
    {
        var f = new EngineFixture();
        var allocator = new CountingAllocator(10);
        var ned = NedMap(f, allocator);
        var a = f.Create();
        var b = f.Create();
        Assert.True(ned.TryGetOrAllocate(a, out ulong keyA));
        Assert.True(ned.Bind(b, 777UL));

        Assert.True(ned.Unbind(a));
        Assert.True(ned.Unbind(b));
        Assert.False(ned.Unbind(a)); // no key any more

        Assert.False(ned.Contains(a));
        Assert.False(ned.TryResolve(keyA, out _));
        Assert.False(ned.TryResolve(777UL, out _));
        Assert.Equal(0, ned.Count);
        Assert.Equal(new[] { keyA }, allocator.Released); // only the allocated key goes back, not the wire key
        Assert.NotNull(f.Engine.State.Find(a)); // the entity itself lives on
        Assert.True(ned.Bind(a, 5UL)); // and can get another key
        Assert.True(ned.TryResolve(5UL, out var back));
        Assert.Equal(a, back);
    }

    [Fact]
    public void Registry_rejects_duplicate_names_and_finds_maps()
    {
        var f = new EngineFixture();
        var ned = NedMap(f);
        Assert.Throws<InvalidOperationException>(() => NedMap(f));
        Assert.Throws<ArgumentException>(() => f.Engine.Identities.Create<ulong>(f.Net, ""));
        Assert.Same(ned, f.Engine.Identities.Find("ned.entity"));
        Assert.Null(f.Engine.Identities.Find("nope"));
        Assert.Single(f.Engine.Identities.All);
    }

    [Fact]
    public void A_buffered_bind_is_visible_to_a_second_handler_of_the_same_message_only_through_the_change_set()
    {
        var f = new EngineFixture();
        var ned = NedMap(f);
        bool committedVisible = true;
        bool pendingVisible = false;
        EntityId resolvedPending = default;
        EntityId created = default;
        f.Module("a", s => s.OnMessage<KeyMsg>((in KeyMsg m, IngressContext ctx) =>
        {
            created = ctx.Changes.CreateEntity();
            ned.Bind(ctx.Changes, created, m.Key);
        }));
        f.Module("b", s => s.OnMessage<KeyMsg>((in KeyMsg m, IngressContext ctx) =>
        {
            committedVisible = ned.TryResolve(m.Key, out _);
            pendingVisible = ned.TryResolve(ctx.Changes, m.Key, out resolvedPending);
            ctx.Changes.Set(resolvedPending, f.Count, EngineFixture.I(5));
        }));

        f.Engine.Ingest(f.Net, new KeyMsg(7));

        Assert.False(committedVisible);
        Assert.True(pendingVisible);
        Assert.Equal(created, resolvedPending);
        Assert.True(ned.TryResolve(7, out var id));
        Assert.Equal(created, id);
        Assert.Equal(5, f.Engine.State.Find(id)!.Get(f.Count).AsInt());
    }

    [Fact]
    public void A_buffered_bind_is_discarded_with_an_aborted_transaction()
    {
        var f = new EngineFixture();
        var ned = NedMap(f);
        f.Module("a", s => s.OnMessage<KeyMsg>((in KeyMsg m, IngressContext ctx) =>
        {
            var id = ctx.Changes.CreateEntity();
            ned.Bind(ctx.Changes, id, m.Key);
        }));
        f.Module("b", s => s.OnMessage<KeyMsg>((in KeyMsg m, IngressContext ctx) => ctx.Abort("no")));

        f.Engine.Ingest(f.Net, new KeyMsg(7));

        Assert.Equal(0, ned.Count);
        Assert.False(ned.TryResolve(7, out _));
        Assert.Equal(0, f.Engine.State.Count);
        Assert.False(f.Engine.State.IsChangeOpen);

        // the same key can be used by the next message: nothing of the aborted one is left
        f.Module("c", s => { });
        var cs = f.Engine.BeginChange(f.Net);
        var e = cs.CreateEntity();
        ned.Bind(cs, e, 7);
        Assert.True(cs.Commit().Succeeded);
        Assert.True(ned.TryResolve(7, out var resolved));
        Assert.Equal(e, resolved);
    }

    [Fact]
    public void A_buffered_bind_is_discarded_when_the_change_set_is_discarded()
    {
        var f = new EngineFixture();
        var ned = NedMap(f);
        var cs = f.Engine.BeginChange(f.Net);
        var e = cs.CreateEntity();
        ned.Bind(cs, e, 1);
        Assert.True(ned.TryResolve(cs, 1, out _));
        cs.Discard();

        var cs2 = f.Engine.BeginChange(f.Net);
        Assert.False(ned.TryResolve(cs2, 1, out _)); // not carried into the next transaction
        cs2.Discard();
        Assert.Equal(0, ned.Count);
    }

    [Fact]
    public void Two_entities_with_one_key_in_one_transaction_reject_the_whole_transaction()
    {
        var f = new EngineFixture();
        var ned = NedMap(f);
        var cs = f.Engine.BeginChange(f.Net);
        var a = cs.CreateEntity();
        var b = cs.CreateEntity();
        cs.Set(a, f.Count, EngineFixture.I(1));
        ned.Bind(cs, a, 9);
        ned.Bind(cs, b, 9);

        var result = cs.Commit();

        Assert.Equal(CommitStatus.Rejected, result.Status);
        Assert.Equal(0, f.Engine.State.Count);
        Assert.Equal(0, ned.Count);
        Assert.False(f.Engine.State.IsChangeOpen);
    }

    [Fact]
    public void A_key_that_is_already_bound_to_another_entity_rejects_the_transaction()
    {
        var f = new EngineFixture();
        var ned = NedMap(f);
        var first = f.Create();
        Assert.True(ned.Bind(first, 9));

        var cs = f.Engine.BeginChange(f.Net);
        var second = cs.CreateEntity();
        cs.Set(second, f.Count, EngineFixture.I(1));
        ned.Bind(cs, second, 9);
        var result = cs.Commit();

        Assert.Equal(CommitStatus.Rejected, result.Status);
        Assert.Equal(1, f.Engine.State.Count);
        Assert.True(ned.TryResolve(9, out var owner));
        Assert.Equal(first, owner);
    }

    [Fact]
    public void A_second_key_for_the_same_entity_in_one_map_rejects_the_transaction()
    {
        var f = new EngineFixture();
        var ned = NedMap(f);

        // two keys in one transaction
        var cs = f.Engine.BeginChange(f.Net);
        var a = cs.CreateEntity();
        ned.Bind(cs, a, 1);
        ned.Bind(cs, a, 2);
        Assert.Equal(CommitStatus.Rejected, cs.Commit().Status);
        Assert.Equal(0, ned.Count);
        Assert.Equal(0, f.Engine.State.Count);

        // a second key for an entity that already has a committed one
        var b = f.Create();
        Assert.True(ned.Bind(b, 5));
        var cs2 = f.Engine.BeginChange(f.Net);
        ned.Bind(cs2, b, 6);
        cs2.Set(b, f.Count, EngineFixture.I(1));
        Assert.Equal(CommitStatus.Rejected, cs2.Commit().Status);
        Assert.True(ned.TryGetKey(b, out var key));
        Assert.Equal(5UL, key);
        Assert.False(ned.TryResolve(6, out _));
        Assert.False(f.Engine.State.Find(b)!.Has(f.Count)); // nothing of the transaction was applied
    }

    [Fact]
    public void Binding_the_same_pair_twice_is_idempotent()
    {
        var f = new EngineFixture();
        var ned = NedMap(f);
        var cs = f.Engine.BeginChange(f.Net);
        var a = cs.CreateEntity();
        ned.Bind(cs, a, 1);
        ned.Bind(cs, a, 1);
        Assert.True(cs.Commit().Succeeded);
        Assert.Equal(1, ned.Count);

        var cs2 = f.Engine.BeginChange(f.Net);
        ned.Bind(cs2, a, 1); // same pair as the committed one
        Assert.True(cs2.Commit().Succeeded);
        Assert.Equal(1, ned.Count);
        Assert.True(ned.Bind(a, 1));
    }

    [Fact]
    public void A_bind_needs_an_entity_that_is_alive_at_the_end_of_the_transaction()
    {
        var f = new EngineFixture();
        var ned = NedMap(f);

        var unknown = f.Engine.BeginChange(f.Net);
        ned.Bind(unknown, new EntityId(999), 1);
        Assert.Equal(CommitStatus.Rejected, unknown.Commit().Status);

        var created = f.Engine.BeginChange(f.Net);
        var destroyedInTx = created.CreateEntity();
        ned.Bind(created, destroyedInTx, 2);
        created.DestroyEntity(destroyedInTx);
        Assert.Equal(CommitStatus.Rejected, created.Commit().Status);

        var existing = f.Create();
        var destroy = f.Engine.BeginChange(f.Net);
        ned.Bind(destroy, existing, 3);
        destroy.DestroyEntity(existing);
        Assert.Equal(CommitStatus.Rejected, destroy.Commit().Status);
        Assert.NotNull(f.Engine.State.Find(existing)); // the destroy was rejected with it

        Assert.Equal(0, ned.Count);
    }

    [Fact]
    public void A_transaction_of_only_binds_commits_as_empty_and_applies_the_bind()
    {
        var f = new EngineFixture();
        var ned = NedMap(f);
        var e = f.Create();
        var records = f.Engine.State.Journal.Count;
        var cs = f.Engine.BeginChange(f.Net);
        ned.Bind(cs, e, 77);

        var result = cs.Commit();

        Assert.Equal(CommitStatus.Empty, result.Status);
        Assert.True(result.Succeeded);
        Assert.Null(result.Record);
        Assert.Equal(records, f.Engine.State.Journal.Count);
        Assert.True(ned.TryResolve(77, out var id));
        Assert.Equal(e, id);
    }

    [Fact]
    public void Binding_in_a_closed_change_set_is_a_programming_error()
    {
        var f = new EngineFixture();
        var ned = NedMap(f);
        var e = f.Create();
        var cs = f.Engine.BeginChange(f.Net);
        cs.Discard();
        Assert.Throws<InvalidOperationException>(() => ned.Bind(cs, e, 1));
    }

    [Fact]
    public void Immediate_binding_validates_entity_key_and_uniqueness()
    {
        var f = new EngineFixture();
        var ned = NedMap(f);
        var a = f.Create();
        var b = f.Create();

        Assert.True(ned.Bind(a, 1));
        Assert.True(ned.Bind(a, 1));                    // same pair: idempotent
        Assert.False(ned.Bind(b, 1));                   // key bound to another entity
        Assert.False(ned.Bind(a, 2));                   // entity already has a key
        Assert.False(ned.Bind(new EntityId(999), 3));   // not alive
        Assert.Equal(1, ned.Count);
        Assert.True(ned.TryResolve(1, out var id));
        Assert.Equal(a, id);
        Assert.False(ned.TryResolve(2, out _));
        Assert.False(ned.TryResolve(3, out _));
    }

    [Fact]
    public void External_allocation_binds_the_key_later_than_the_entity_appears()
    {
        var f = new EngineFixture();
        var ned = NedMap(f);
        var handle = f.Engine.Identities.Create<RprHandle>(f.Proc, "rpr.handle");
        var e = f.Create();
        Assert.False(handle.TryGetKey(e, out _));

        // the transport hands out the handle after the entity was published
        Assert.True(handle.Bind(e, new RprHandle(0x2A01)));

        Assert.True(handle.TryGetKey(e, out var h));
        Assert.Equal(0x2A01u, h.Value);
        Assert.False(handle.TryGetOrAllocate(f.Create(), out _)); // no allocator: an external map never allocates
    }

    [Fact]
    public void Event_of_the_message_that_destroyed_its_target_can_still_translate_the_target()
    {
        // e.g. a detonation whose transaction also destroys the target: the event is dispatched after the
        // record, and its egress still needs the target's key; the next message no longer resolves it.
        var f = new EngineFixture();
        var ned = NedMap(f, null);
        var target = f.Create();
        Assert.True(ned.Bind(target, 7));
        bool translated = false;
        f.Module("ingress", s => s.OnMessage<PingMsg>((in PingMsg m, IngressContext ctx) =>
        {
            ctx.Changes.DestroyEntity(target);
            ctx.Publish(f.Fired, ctx.RentPayload().Set(f.Label, AttrValue.From("x")));
        }));
        f.Module("egress", s => s.OnEvent(f.Fired).Do(_ => translated = ned.TryGetKey(target, out var key) && key == 7));

        f.Engine.Ingest(f.Net, new PingMsg(1));

        Assert.True(translated);
        Assert.False(ned.TryGetKey(target, out _));
        Assert.False(ned.TryResolve(7, out _));
    }

    [Fact]
    public void Keys_stay_resolvable_in_removal_rules_and_are_released_afterwards_only_when_allocated()
    {
        var f = new EngineFixture();
        var allocator = new CountingAllocator(10);
        var ned = NedMap(f, allocator);
        var seenInRemoval = new List<ulong>();
        bool resolvedInRemoval = true;
        int releasedDuringRemoval = 0;
        // rule exceptions are swallowed, so the checks are recorded and asserted below
        f.Module("egress", s => s.OnEntityRemoved().Do(t =>
        {
            if (!ned.TryGetKey(t.Id, out var key))
            {
                resolvedInRemoval = false;
                return;
            }

            seenInRemoval.Add(key);
            resolvedInRemoval &= ned.TryResolve(key, out var id) && id == t.Id;
            releasedDuringRemoval += allocator.Released.Count; // not released yet
        }));
        var wire = f.Create();
        var own = f.Create();
        Assert.True(ned.Bind(wire, 5));                  // a key received from the wire
        Assert.True(ned.TryGetOrAllocate(own, out var allocated));
        Assert.Equal(1000UL, allocated);

        var cs = f.Engine.BeginChange(f.Net);
        cs.DestroyEntity(wire);
        cs.DestroyEntity(own);
        Assert.True(cs.Commit().Succeeded);

        Assert.Equal(0, f.Engine.FindRule("egress/OnEntityRemoved#1")!.Failures);
        Assert.True(resolvedInRemoval);
        Assert.Equal(0, releasedDuringRemoval);
        Assert.Equal(new ulong[] { 5, 1000 }, seenInRemoval.Order().ToArray());
        Assert.Equal(0, ned.Count);
        Assert.False(ned.TryResolve(5, out _));
        Assert.False(ned.TryResolve(1000, out _));
        Assert.Equal(new[] { 1000UL }, allocator.Released); // the wire key is not the allocator's
    }

    [Fact]
    public void Unload_releases_the_keys_of_every_entity()
    {
        var f = new EngineFixture();
        var allocator = new CountingAllocator(10);
        var ned = NedMap(f, allocator);
        var a = f.Create();
        var b = f.Create();
        Assert.True(ned.TryGetOrAllocate(a, out _));
        Assert.True(ned.TryGetOrAllocate(b, out _));
        Assert.True(f.Engine.Execute(new SessionCommand(SessionCommandKind.Load, "s", SessionMode.Live), f.Net));
        Assert.True(f.Engine.Execute(new SessionCommand(SessionCommandKind.Unload, null, SessionMode.Live), f.Net));

        Assert.Equal(0, f.Engine.State.Count);
        Assert.Equal(0, ned.Count);
        Assert.Equal(2, allocator.Released.Count);
    }

    [Fact]
    public void Allocation_returns_the_existing_key_and_fails_without_allocator_exhausted_or_dead_entity()
    {
        var f = new EngineFixture();
        var allocator = new CountingAllocator(2);
        var ned = NedMap(f, allocator);
        var a = f.Create();
        var b = f.Create();
        var c = f.Create();

        Assert.True(ned.TryGetOrAllocate(a, out var k1));
        Assert.True(ned.TryGetOrAllocate(a, out var again));
        Assert.Equal(k1, again);
        Assert.Equal(1, allocator.Allocated);

        Assert.True(ned.TryGetOrAllocate(b, out var k2));
        Assert.NotEqual(k1, k2);
        Assert.False(ned.TryGetOrAllocate(c, out _));            // exhausted
        Assert.False(ned.TryGetKey(c, out _));
        Assert.False(ned.TryGetOrAllocate(new EntityId(999), out _)); // not alive
        Assert.Equal(2, ned.Count);

        var noAllocator = f.Engine.Identities.Create<string>(f.Net, "plain");
        Assert.False(noAllocator.TryGetOrAllocate(a, out _));
    }

    [Fact]
    public void An_allocator_handing_out_a_key_in_use_is_refused_and_the_key_given_back()
    {
        var f = new EngineFixture();
        var allocator = new CountingAllocator(5);
        var ned = NedMap(f, allocator);
        var a = f.Create();
        Assert.True(ned.Bind(a, 1000)); // the key the allocator will offer first
        var b = f.Create();

        Assert.False(ned.TryGetOrAllocate(b, out _));
        Assert.Equal(new[] { 1000UL }, allocator.Released);
        Assert.False(ned.TryGetKey(b, out _));
    }

    [Fact]
    public void A_quarantining_allocator_reuses_a_number_only_after_the_quarantine()
    {
        var f = new EngineFixture();
        var allocator = new QuarantineAllocator(() => f.Clock.Now, quarantine: 30, max: 1);
        var dis = f.Engine.Identities.Create<DisId>(f.Proc, "rpr.entityId", allocator);
        var a = f.Create();
        Assert.True(dis.TryGetOrAllocate(a, out var first));
        Assert.Equal(new DisId(1, 3001, 1), first);

        var b = f.Create();
        Assert.False(dis.TryGetOrAllocate(b, out _)); // pool of one is exhausted

        var cs = f.Engine.BeginChange(f.Net);
        cs.DestroyEntity(a);
        Assert.True(cs.Commit().Succeeded);                 // released now, free again at t + 30

        f.Clock.Advance(10);
        Assert.False(dis.TryGetOrAllocate(b, out _));       // still in quarantine
        f.Clock.Advance(20);
        Assert.True(dis.TryGetOrAllocate(b, out var reused));
        Assert.Equal(first, reused);
    }

    [Fact]
    public void A_composite_key_names_a_part_of_a_formation()
    {
        var f = new EngineFixture();
        var members = f.Engine.Identities.Create<MemberKey>(f.Net, "ned.member");
        var cs = f.Engine.BeginChange(f.Net);
        var m0 = cs.CreateEntity();
        var m1 = cs.CreateEntity();
        members.Bind(cs, m0, new MemberKey(7, 0));
        members.Bind(cs, m1, new MemberKey(7, 1));
        Assert.True(cs.Commit().Succeeded);

        Assert.True(members.TryResolve(new MemberKey(7, 1), out var id));
        Assert.Equal(m1, id);
        Assert.False(members.TryResolve(new MemberKey(8, 1), out _));
        Assert.True(members.TryGetKey(m0, out var key));
        Assert.Equal(new MemberKey(7, 0), key);
    }

    [Fact]
    public void A_cascade_overflow_refuses_a_bind_only_transaction_too()
    {
        var f = new EngineFixture(new GatewayOptions { MaxCascadeDepth = 0 });
        var ned = NedMap(f);
        var e = f.Create();
        // a rule that commits (depth 1 > 0) is refused before anything is applied, including the bind
        var status = CommitStatus.Committed;
        f.Module("m", s => s.OnAttributesChanged(f.Count).Do(t =>
        {
            var cs = f.Engine.BeginChange(f.Proc);
            ned.Bind(cs, t.Id, 1);
            status = cs.Commit().Status;
        }));
        Assert.True(f.Write(e, f.Count, EngineFixture.I(1)).Succeeded);
        Assert.Equal(CommitStatus.Rejected, status);
        Assert.Equal(0, ned.Count);
    }
}
