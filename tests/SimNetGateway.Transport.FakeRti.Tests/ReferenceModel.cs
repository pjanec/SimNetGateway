using SimNetGateway.Protocol.Rpr.Fom;
using SimNetGateway.Protocol.Rpr.Hla;

namespace SimNetGateway.Transport.FakeRti.Tests;

/// <summary>
/// The brute-force reference model of the RTI semantics of DD-008 section 3.6: simple, slow, and written independently of
/// the fake RTI (no shared code except the immutable FOM class tree). Every operation re-derives what it needs by scanning
/// plain lists; the model returns the name of the exception the real RTI must throw (or null) and queues the canonical
/// text of every callback it expects, per federate, in order.
/// </summary>
internal sealed class RefModel
{
    private sealed class Amb
    {
        public bool Connected;
        public Member? Member;
        public readonly List<string> Delivered = new();
    }

    private sealed class Member
    {
        public Member(Amb amb, Fed federation, int handle, string name)
        {
            Amb = amb;
            Federation = federation;
            Handle = handle;
            Name = name;
        }

        public Amb Amb { get; }
        public Fed Federation { get; }
        public int Handle { get; }
        public string Name { get; }
        public Dictionary<FomObjectClass, HashSet<FomAttribute>> Published { get; } = new();
        public Dictionary<FomObjectClass, HashSet<FomAttribute>> Subscribed { get; } = new();
        public HashSet<FomInteractionClass> PublishedInteractions { get; } = new();
        public HashSet<FomInteractionClass> SubscribedInteractions { get; } = new();
        public List<string> Queue { get; } = new();
    }

    private sealed class Obj
    {
        public int Id;
        public string Name = "";
        public FomObjectClass Class = null!;
        public Member? Registrant;
        public Member? Owner;
        public HashSet<FomAttribute> Owned = new();
        public bool Alive = true;
        public readonly Dictionary<Member, FomObjectClass> Known = new();
    }

    private sealed class NameSlot
    {
        public Member? ReservedBy;
        public Obj? Object;
    }

    private sealed class Fed
    {
        public string Name = "";
        public readonly List<Member> Members = new();
        public int NextHandle = 1;
        public readonly List<Obj> Objects = new();
        public readonly Dictionary<string, NameSlot> Names = new();
    }

    private readonly Amb[] _ambs;
    private readonly Dictionary<string, Fed> _federations = new();
    private readonly FomAttribute _privilege;

    public RefModel(FomModel fom, int ambassadors, IReadOnlyCollection<string> goodModules, string conflictingModule)
    {
        Fom = fom;
        GoodModules = goodModules;
        ConflictingModule = conflictingModule;
        _ambs = Enumerable.Range(0, ambassadors).Select(_ => new Amb()).ToArray();
        _privilege = fom.ObjectRoot.FindAttribute(FomModel.PrivilegeToDeleteObjectName)!;
    }

    public FomModel Fom { get; }

    public IReadOnlyCollection<string> GoodModules { get; }

    public string ConflictingModule { get; }

    // --- counters for coverage assertions ---

    public int Discoveries { get; private set; }
    public int Promotions { get; private set; }
    public int Reflections { get; private set; }
    public int Removals { get; private set; }
    public int Interactions { get; private set; }
    public int Provides { get; private set; }
    public int Reservations { get; private set; }

    // --- read access for the generator and the comparison ---

    public IReadOnlyList<string> Delivered(int amb) => _ambs[amb].Delivered;

    public int Pending(int amb) => _ambs[amb].Member?.Queue.Count ?? 0;

    public string? FederationOf(int amb) => _ambs[amb].Member?.Federation.Name;

    public int? FederateHandle(int amb) => _ambs[amb].Member?.Handle;

    public bool IsMember(int amb) => _ambs[amb].Member is not null;

    public IReadOnlyList<string> FederationNames => _federations.Keys.Order(StringComparer.Ordinal).ToList();

    /// <summary>Handle numbers of the instances registered so far in the federation the ambassador is in (or all, if none).</summary>
    public int ObjectCount(int amb) => _ambs[amb].Member?.Federation.Objects.Count ?? 0;

    public string? ObjectName(string federation, int handle) =>
        _federations.TryGetValue(federation, out var f) && handle >= 1 && handle <= f.Objects.Count ? f.Objects[handle - 1].Name : null;

    /// <summary>Handles of instances the ambassador registered and still owns.</summary>
    public IReadOnlyList<int> OwnedObjects(int amb) =>
        _ambs[amb].Member is { } m ? m.Federation.Objects.Where(o => o.Alive && o.Owner == m).Select(o => o.Id).ToList() : [];

    /// <summary>Class the ambassador knows the instance as, or null.</summary>
    public FomObjectClass? KnownClass(int amb, int handle)
    {
        var m = _ambs[amb].Member;
        if (m is null || handle < 1 || handle > m.Federation.Objects.Count)
        {
            return null;
        }
        var o = m.Federation.Objects[handle - 1];
        return o.Alive ? ClassKnownBy(o, m) : null;
    }

    public IReadOnlyList<string> PublishedClasses(int amb) =>
        _ambs[amb].Member is { } m ? m.Published.Keys.Select(c => c.FullName).Order(StringComparer.Ordinal).ToList() : [];

    public IReadOnlyList<string> PublishedInteractionClasses(int amb) =>
        _ambs[amb].Member is { } m ? m.PublishedInteractions.Select(c => c.FullName).Order(StringComparer.Ordinal).ToList() : [];

    /// <summary>Handles of the live instances the ambassador registered or discovered.</summary>
    public IReadOnlyList<int> KnownObjects(int amb) =>
        _ambs[amb].Member is { } m ? m.Federation.Objects.Where(o => Find(m, o.Id) is not null).Select(o => o.Id).ToList() : [];

    /// <summary>The attributes of an instance the ambassador owns, as (declaring class, name); empty when it owns nothing.</summary>
    public IReadOnlyList<(string Declaring, string Name)> OwnedAttributes(int amb, int handle)
    {
        var m = _ambs[amb].Member;
        if (m is null || handle < 1 || handle > m.Federation.Objects.Count)
        {
            return [];
        }
        var o = m.Federation.Objects[handle - 1];
        return o.Alive && o.Owner == m
            ? o.Owned.Select(a => (a.DeclaringClass.FullName, a.Name)).Order().ToList()
            : [];
    }

    // --- operations: each returns the exception type name the RTI must throw, or null ---

    public string? Connect(int a)
    {
        var amb = _ambs[a];
        if (amb.Connected)
        {
            return nameof(AlreadyConnected);
        }
        amb.Connected = true;
        return null;
    }

    public string? Create(int a, string federation, IReadOnlyList<string> modules)
    {
        if (!_ambs[a].Connected)
        {
            return nameof(NotConnected);
        }
        if (_federations.ContainsKey(federation))
        {
            return nameof(FederationExecutionAlreadyExists);
        }
        if (modules.Any(m => m != ConflictingModule && !GoodModules.Contains(m)))
        {
            return nameof(NameNotFound);
        }
        if (modules.Contains(ConflictingModule) && modules.Any(GoodModules.Contains))
        {
            return nameof(InconsistentFdd);
        }
        _federations[federation] = new Fed { Name = federation };
        return null;
    }

    public string? Destroy(int a, string federation)
    {
        if (!_ambs[a].Connected)
        {
            return nameof(NotConnected);
        }
        if (!_federations.TryGetValue(federation, out var f))
        {
            return nameof(FederationExecutionDoesNotExist);
        }
        if (f.Members.Count > 0)
        {
            return nameof(FederatesCurrentlyJoined);
        }
        _federations.Remove(federation);
        return null;
    }

    /// <summary>Joins; the handle the real RTI must return is <paramref name="handle"/>.</summary>
    public string? Join(int a, string name, string federation, out int handle)
    {
        handle = 0;
        var amb = _ambs[a];
        if (!amb.Connected)
        {
            return nameof(NotConnected);
        }
        if (amb.Member is not null)
        {
            return nameof(FederateAlreadyExecutionMember);
        }
        if (!_federations.TryGetValue(federation, out var f))
        {
            return nameof(FederationExecutionDoesNotExist);
        }
        if (f.Members.Any(m => m.Name == name))
        {
            return nameof(FederateNameAlreadyInUse);
        }
        var member = new Member(amb, f, f.NextHandle++, name);
        f.Members.Add(member);
        amb.Member = member;
        handle = member.Handle;
        return null;
    }

    public string? Resign(int a, ResignAction action)
    {
        var pre = Need(a, member: true, out var me);
        if (pre is not null)
        {
            return pre;
        }
        var f = me!.Federation;
        var owned = f.Objects.Where(o => o.Alive && o.Owner == me).ToList();
        var deletes = action is ResignAction.DeleteObjects or ResignAction.DeleteObjectsThenDivest or ResignAction.CancelThenDeleteThenDivest;
        if (!deletes && action != ResignAction.UnconditionallyDivestAttributes && owned.Count > 0)
        {
            return nameof(FederateOwnsAttributes);
        }
        foreach (var o in owned)
        {
            if (deletes)
            {
                Kill(o, []);
            }
            else
            {
                o.Owner = null;
                o.Registrant = null;
                o.Owned = new HashSet<FomAttribute>();
            }
        }
        foreach (var o in f.Objects.Where(x => x.Alive))
        {
            o.Known.Remove(me);
        }
        foreach (var key in f.Names.Where(p => p.Value.ReservedBy == me && p.Value.Object is null).Select(p => p.Key).ToList())
        {
            f.Names.Remove(key);
        }
        f.Members.Remove(me);
        me.Queue.Clear();
        me.Amb.Member = null;
        return null;
    }

    public string? GetClass(int a, string name) =>
        Need(a, member: true, out _) ?? (Fom.FindObjectClass(name) is null ? nameof(NameNotFound) : null);

    public string? GetAttribute(int a, string? cls, string name)
    {
        var pre = Need(a, member: true, out _);
        if (pre is not null)
        {
            return pre;
        }
        if (cls is null)
        {
            return nameof(InvalidObjectClassHandle);
        }
        return Fom.FindObjectClass(cls)!.FindAttribute(name) is null ? nameof(NameNotFound) : null;
    }

    public string? GetInteraction(int a, string name) =>
        Need(a, member: true, out _) ?? (Fom.FindInteractionClass(name) is null ? nameof(NameNotFound) : null);

    public string? GetParameter(int a, string? cls, string name)
    {
        var pre = Need(a, member: true, out _);
        if (pre is not null)
        {
            return pre;
        }
        if (cls is null)
        {
            return nameof(InvalidInteractionClassHandle);
        }
        return Fom.FindInteractionClass(cls)!.FindParameter(name) is null ? nameof(NameNotFound) : null;
    }

    /// <summary>Publish (<paramref name="subscribe"/> false) or subscribe; an empty set removes the declaration.</summary>
    public string? Declare(int a, bool subscribe, string? cls, IReadOnlyList<(string? Declaring, string Name)> attributes)
    {
        var pre = Need(a, member: true, out var me);
        if (pre is not null)
        {
            return pre;
        }
        if (cls is null)
        {
            return nameof(ObjectClassNotDefined);
        }
        var c = Fom.FindObjectClass(cls)!;
        var set = new HashSet<FomAttribute>();
        foreach (var (declaring, name) in attributes)
        {
            var attribute = declaring is null ? null : Fom.FindObjectClass(declaring)!.DeclaredAttributes.First(x => x.Name == name);
            if (attribute is null || !c.AllAttributes.Contains(attribute))
            {
                return nameof(AttributeNotDefined);
            }
            set.Add(attribute);
        }
        var table = subscribe ? me!.Subscribed : me!.Published;
        if (set.Count == 0)
        {
            table.Remove(c);
        }
        else
        {
            table[c] = set;
            if (subscribe)
            {
                foreach (var o in me.Federation.Objects.Where(x => x.Alive))
                {
                    MaybeDiscover(o, me);
                }
            }
        }
        return null;
    }

    public string? Undeclare(int a, bool subscribe, string? cls)
    {
        var pre = Need(a, member: true, out var me);
        if (pre is not null)
        {
            return pre;
        }
        if (cls is null)
        {
            return nameof(ObjectClassNotDefined);
        }
        (subscribe ? me!.Subscribed : me!.Published).Remove(Fom.FindObjectClass(cls)!);
        return null;
    }

    public string? DeclareInteraction(int a, bool subscribe, bool add, string? cls)
    {
        var pre = Need(a, member: true, out var me);
        if (pre is not null)
        {
            return pre;
        }
        if (cls is null)
        {
            return nameof(InteractionClassNotDefined);
        }
        var c = Fom.FindInteractionClass(cls)!;
        var set = subscribe ? me!.SubscribedInteractions : me!.PublishedInteractions;
        if (add)
        {
            set.Add(c);
        }
        else
        {
            set.Remove(c);
        }
        return null;
    }

    public string? Reserve(int a, string name)
    {
        var pre = Need(a, member: true, out var me);
        if (pre is not null)
        {
            return pre;
        }
        if (!LegalName(name))
        {
            return nameof(IllegalName);
        }
        if (me!.Federation.Names.ContainsKey(name))
        {
            me.Queue.Add("RES- " + name);
        }
        else
        {
            me.Federation.Names[name] = new NameSlot { ReservedBy = me };
            me.Queue.Add("RES+ " + name);
            Reservations++;
        }
        return null;
    }

    /// <summary>Registers; <paramref name="id"/> is the handle number the real RTI must return.</summary>
    public string? Register(int a, string? cls, string? name, out int id)
    {
        id = 0;
        var pre = Need(a, member: true, out var me);
        if (pre is not null)
        {
            return pre;
        }
        if (cls is null)
        {
            return nameof(ObjectClassNotDefined);
        }
        var c = Fom.FindObjectClass(cls)!;
        if (!me!.Published.TryGetValue(c, out var published))
        {
            return nameof(ObjectClassNotPublished);
        }
        var f = me.Federation;
        NameSlot? slot = null;
        if (name is not null)
        {
            if (!LegalName(name))
            {
                return nameof(IllegalName);
            }
            f.Names.TryGetValue(name, out slot);
            if (slot?.Object is not null)
            {
                return nameof(ObjectInstanceNameInUse);
            }
            if (slot is null || slot.ReservedBy != me)
            {
                return nameof(ObjectInstanceNameNotReserved);
            }
        }
        var o = new Obj { Id = f.Objects.Count + 1, Class = c, Registrant = me, Owner = me };
        o.Name = name ?? "HLAobject_" + o.Id;
        o.Owned = new HashSet<FomAttribute>(published) { _privilege };
        f.Objects.Add(o);
        if (slot is null)
        {
            f.Names[o.Name] = slot = new NameSlot();
        }
        slot.Object = o;
        foreach (var other in f.Members.OrderBy(m => m.Handle).Where(m => m != me))
        {
            MaybeDiscover(o, other);
        }
        id = o.Id;
        return null;
    }

    public string? GetObjectName(int a, int handle, out string? name)
    {
        name = null;
        var pre = Need(a, member: true, out var me);
        if (pre is not null)
        {
            return pre;
        }
        var o = Find(me!, handle);
        if (o is null)
        {
            return nameof(ObjectInstanceNotKnown);
        }
        name = o.Name;
        return null;
    }

    public string? Update(int a, int handle, IReadOnlyList<(string? Declaring, string Name, byte[] Value)> items, byte[] tag)
    {
        var pre = Need(a, member: true, out var me);
        if (pre is not null)
        {
            return pre;
        }
        var o = Find(me!, handle);
        if (o is null)
        {
            return nameof(ObjectInstanceNotKnown);
        }
        var known = ClassKnownBy(o, me!)!;
        var values = new Dictionary<FomAttribute, byte[]>();
        foreach (var (declaring, name, value) in items)
        {
            var attribute = declaring is null ? null : Fom.FindObjectClass(declaring)!.DeclaredAttributes.First(x => x.Name == name);
            if (attribute is null || !known.AllAttributes.Contains(attribute))
            {
                return nameof(AttributeNotDefined);
            }
            values[attribute] = value;
        }
        if (values.Keys.Any(k => o.Owner != me || !o.Owned.Contains(k)))
        {
            return nameof(AttributeNotOwned);
        }
        foreach (var other in me!.Federation.Members.OrderBy(m => m.Handle).Where(m => m != me && o.Known.ContainsKey(m)))
        {
            if (!other.Subscribed.TryGetValue(o.Known[other], out var subscription))
            {
                continue;
            }
            var delivered = values.Where(p => subscription.Contains(p.Key)).ToList();
            if (delivered.Count > 0)
            {
                other.Queue.Add($"REFL {o.Name} {string.Join(",", delivered.Select(p => Key(p.Key) + "=" + Hex(p.Value)).Order(StringComparer.Ordinal))} tag={Hex(tag)}");
                Reflections++;
            }
        }
        return null;
    }

    public string? Delete(int a, int handle, byte[] tag)
    {
        var pre = Need(a, member: true, out var me);
        if (pre is not null)
        {
            return pre;
        }
        var o = Find(me!, handle);
        if (o is null)
        {
            return nameof(ObjectInstanceNotKnown);
        }
        if (o.Owner != me)
        {
            return nameof(DeletePrivilegeNotHeld);
        }
        Kill(o, tag);
        return null;
    }

    public string? Send(int a, string? cls, IReadOnlyList<(string? Declaring, string Name, byte[] Value)> items, byte[] tag)
    {
        var pre = Need(a, member: true, out var me);
        if (pre is not null)
        {
            return pre;
        }
        if (cls is null)
        {
            return nameof(InteractionClassNotDefined);
        }
        var c = Fom.FindInteractionClass(cls)!;
        if (!me!.PublishedInteractions.Contains(c))
        {
            return nameof(InteractionClassNotPublished);
        }
        var values = new Dictionary<FomParameter, byte[]>();
        foreach (var (declaring, name, value) in items)
        {
            var parameter = declaring is null ? null : Fom.FindInteractionClass(declaring)!.DeclaredParameters.First(x => x.Name == name);
            if (parameter is null || !c.AllParameters.Contains(parameter))
            {
                return nameof(InteractionParameterNotDefined);
            }
            values[parameter] = value;
        }
        foreach (var other in me.Federation.Members.OrderBy(m => m.Handle).Where(m => m != me))
        {
            FomInteractionClass? received = null;
            for (var k = c; k is not null && received is null; k = k.Parent)
            {
                if (other.SubscribedInteractions.Contains(k))
                {
                    received = k;
                }
            }
            if (received is null)
            {
                continue;
            }
            var delivered = values.Where(p => received.AllParameters.Contains(p.Key)).ToList();
            other.Queue.Add($"INT {received.FullName} {string.Join(",", delivered.Select(p => Key(p.Key) + "=" + Hex(p.Value)).Order(StringComparer.Ordinal))} tag={Hex(tag)}");
            Interactions++;
        }
        return null;
    }

    public string? Request(int a, int handle, IReadOnlyList<(string? Declaring, string Name)> attributes, byte[] tag)
    {
        var pre = Need(a, member: true, out var me);
        if (pre is not null)
        {
            return pre;
        }
        var o = Find(me!, handle);
        if (o is null)
        {
            return nameof(ObjectInstanceNotKnown);
        }
        var requested = new HashSet<FomAttribute>();
        foreach (var (declaring, name) in attributes)
        {
            var attribute = declaring is null ? null : Fom.FindObjectClass(declaring)!.DeclaredAttributes.First(x => x.Name == name);
            if (attribute is null || !ClassKnownBy(o, me!)!.AllAttributes.Contains(attribute))
            {
                return nameof(AttributeNotDefined);
            }
            requested.Add(attribute);
        }
        ProvideTo(o, me!, requested, tag);
        return null;
    }

    public string? RequestClass(int a, string? cls, IReadOnlyList<(string? Declaring, string Name)> attributes, byte[] tag)
    {
        var pre = Need(a, member: true, out var me);
        if (pre is not null)
        {
            return pre;
        }
        if (cls is null)
        {
            return nameof(ObjectClassNotDefined);
        }
        var c = Fom.FindObjectClass(cls)!;
        var requested = new HashSet<FomAttribute>();
        foreach (var (declaring, name) in attributes)
        {
            var attribute = declaring is null ? null : Fom.FindObjectClass(declaring)!.DeclaredAttributes.First(x => x.Name == name);
            if (attribute is null || !c.AllAttributes.Contains(attribute))
            {
                return nameof(AttributeNotDefined);
            }
            requested.Add(attribute);
        }
        foreach (var o in me!.Federation.Objects.Where(x => x.Alive && Find(me, x.Id) is not null && x.Class.SelfAndAncestors.Contains(c)))
        {
            ProvideTo(o, me, requested, tag);
        }
        return null;
    }

    /// <summary>Evokes one callback (<paramref name="all"/> false) or everything queued; <paramref name="more"/> is the expected result.</summary>
    public string? Evoke(int a, bool all, out bool more)
    {
        more = false;
        var pre = Need(a, member: false, out _);
        if (pre is not null)
        {
            return pre;
        }
        var amb = _ambs[a];
        var queue = amb.Member?.Queue;
        if (queue is not null)
        {
            var count = all ? queue.Count : Math.Min(1, queue.Count);
            amb.Delivered.AddRange(queue.Take(count));
            queue.RemoveRange(0, count);
            more = queue.Count > 0;
        }
        return null;
    }

    // --- helpers ---

    private string? Need(int a, bool member, out Member? me)
    {
        var amb = _ambs[a];
        me = amb.Member;
        if (!amb.Connected)
        {
            return nameof(NotConnected);
        }
        if (member && me is null)
        {
            return nameof(FederateNotExecutionMember);
        }
        return null;
    }

    private static Obj? Find(Member me, int handle)
    {
        if (handle < 1 || handle > me.Federation.Objects.Count)
        {
            return null;
        }
        var o = me.Federation.Objects[handle - 1];
        return o.Alive && (o.Registrant == me || o.Known.ContainsKey(me)) ? o : null;
    }

    private static FomObjectClass? ClassKnownBy(Obj o, Member m) => o.Registrant == m ? o.Class : o.Known.GetValueOrDefault(m);

    private void MaybeDiscover(Obj o, Member m)
    {
        if (o.Registrant == m || o.Known.ContainsKey(m))
        {
            return;
        }
        FomObjectClass? closest = null;
        for (var c = o.Class; c is not null && closest is null; c = c.Parent)
        {
            if (m.Subscribed.ContainsKey(c))
            {
                closest = c;
            }
        }
        if (closest is null)
        {
            return;
        }
        o.Known[m] = closest;
        m.Queue.Add($"DISC {o.Name} as {closest.FullName}");
        Discoveries++;
        if (closest != o.Class)
        {
            Promotions++;
        }
    }

    private void Kill(Obj o, byte[] tag)
    {
        o.Alive = false;
        foreach (var m in o.Known.Keys.OrderBy(m => m.Handle))
        {
            m.Queue.Add($"REM {o.Name} tag={Hex(tag)}");
            Removals++;
        }
        o.Known.Clear();
        var fed = _federations.Values.First(f => f.Objects.Contains(o));
        fed.Names.Remove(o.Name);
    }

    private void ProvideTo(Obj o, Member requester, HashSet<FomAttribute> requested, byte[] tag)
    {
        if (o.Owner is null || o.Owner == requester)
        {
            return;
        }
        var provided = requested.Where(o.Owned.Contains).Select(Key).Order(StringComparer.Ordinal).ToList();
        if (provided.Count > 0)
        {
            o.Owner.Queue.Add($"PROV {o.Name} {string.Join(",", provided)} tag={Hex(tag)}");
            Provides++;
        }
    }

    private static bool LegalName(string n)
    {
        if (n.Length == 0 || n.Length > 255 || n.StartsWith("HLA", StringComparison.Ordinal))
        {
            return false;
        }
        foreach (var ch in n)
        {
            if (ch < 32 || ch > 126)
            {
                return false;
            }
        }
        return true;
    }

    public static string Key(FomAttribute a) => a.DeclaringClass.FullName + "#" + a.Name;

    public static string Key(FomParameter p) => p.DeclaringClass.FullName + "#" + p.Name;

    public static string Hex(byte[] bytes) => Convert.ToHexString(bytes);
}
