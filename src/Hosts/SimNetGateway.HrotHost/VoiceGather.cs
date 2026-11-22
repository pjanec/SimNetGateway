using Fdp.Core;
using Fdp.Toolkit.Behavior;
using Fdp.Toolkit.Civilians;
using Fdp.Toolkit.Replication.Components;
using Fdp.Toolkit.Squad.Command;
using SimNetGateway.Engine.InMemory;
using SimNetGateway.Protocol.Hrot;

namespace SimNetGateway.HrotHost;

/// <summary>
/// What HROT's men shout, report and order (DD-026), read from HROT's own state and never decided here: a man's <c>Utterance</c> component (a NEW utterance is a change of kind, target or end time:
/// <c>Utterances.Say</c> always writes a new end time, so an identical shout after the first was over is new, and a held one is not), and each unit's <c>CommandReportHistory</c> / <c>CommandOrderHistory</c> (newest
/// first: the entries in front of the newest one seen last gather are new, sent oldest first). A history entry is written twice by HROT (outgoing on the sender, incoming on the addressee, whichever of them keeps a
/// history): this gather sends the outgoing one, and the incoming one only when the sender keeps no history, so a report or an order is ONE event. A re-sent order (HROT counts it in <c>Repeats</c>) is not news. An order is
/// named by the node's <c>BehaviorRegistry</c> (a hash the registry does not know is not sent: names, not numbers).
/// <para>The first gather of a world (and the first after <see cref="Clear"/>, a world boundary) only remembers: nothing that already happened is replayed. A speaker or a unit first seen later is remembered silently too
/// (a speaker whose shout is still going, <c>Until</c> after now, is the exception: it is said). A steady gather allocates nothing. Main thread only.</para>
/// </summary>
internal sealed class VoiceGather
{
    private sealed class Speaker
    {
        public UtteranceKind Kind;
        public int Target;
        public double Until;
        public int Stamp;
    }

    private readonly record struct ReportKey(double At, int From, int To, uint Seq, byte Traffic, byte Kind, byte Sub);

    private readonly record struct OrderKey(double At, int Counterpart, int Behaviour, byte Traffic);

    private sealed class Cursor<TKey> where TKey : struct
    {
        public TKey Newest;
        public bool Has;
        public int Stamp;
    }

    private readonly Dictionary<int, Speaker> _speakers = new();
    private readonly Dictionary<ulong, Cursor<ReportKey>> _reports = new();
    private readonly Dictionary<ulong, Cursor<OrderKey>> _orders = new();
    private readonly HashSet<int> _reportHolders = new(), _orderHolders = new();
    private readonly List<ulong> _scratch = new();
    private readonly List<int> _scratchIds = new();
    private readonly string?[] _kindNames = new string?[256];
    private EntityRepository? _world;
    private EntityQuery? _speakerQuery, _reportQuery, _orderQuery;
    private bool _primed;
    private int _stamp;

    /// <summary>How many voice events were sent (utterances, reports, orders), for the tests and the counters.</summary>
    public int UtterancesSent { get; private set; }

    public int ReportsSent { get; private set; }

    public int OrdersSent { get; private set; }

    /// <summary>A world boundary: forget everything, so nothing is replayed and the next gather only remembers.</summary>
    public void Clear()
    {
        _speakers.Clear();
        _reports.Clear();
        _orders.Clear();
        _reportHolders.Clear();
        _orderHolders.Clear();
        _world = null;
        _speakerQuery = _reportQuery = _orderQuery = null;
        _primed = false;
    }

    public void Gather(EntityRepository world, InMemoryInbound inbound, double now, BehaviorRegistry? behaviors, Func<int, bool> isReplica)
    {
        Bind(world);
        _stamp++;
        bool announce = _primed;
        if (_speakerQuery is not null)
        {
            GatherSpeakers(world, inbound, now, announce, isReplica);
        }

        if (_reportQuery is not null)
        {
            GatherReports(world, inbound, announce, isReplica);
        }

        if (_orderQuery is not null)
        {
            GatherOrders(world, inbound, announce, behaviors, isReplica);
        }

        _primed = true;
    }

    private void Bind(EntityRepository world)
    {
        if (ReferenceEquals(_world, world))
        {
            return;
        }

        Clear();
        _world = world;
        _speakerQuery = world.IsComponentTypeRegistered<Utterance>() ? world.Query().With<Utterance>().With<NetworkIdentity>().Build() : null;
        _reportQuery = world.IsComponentTypeRegistered<CommandReportHistory>() ? world.Query().With<CommandReportHistory>().Build() : null;
        _orderQuery = world.IsComponentTypeRegistered<CommandOrderHistory>() ? world.Query().With<CommandOrderHistory>().Build() : null;
    }

    // ---- utterances -------------------------------------------------------------------------------------------------

    private void GatherSpeakers(EntityRepository world, InMemoryInbound inbound, double now, bool announce, Func<int, bool> isReplica)
    {
        foreach (var e in _speakerQuery!)
        {
            int id = world.GetComponentRO<NetworkIdentity>(e).Value;
            if (id == 0 || isReplica(id))
            {
                continue;
            }

            ref readonly var u = ref world.GetComponentRO<Utterance>(e);
            int target = TargetOf(world, u.Target);
            double until = (double)u.Until;
            bool isNew = !_speakers.TryGetValue(id, out var s);
            if (isNew)
            {
                s = new Speaker { Kind = u.Kind, Target = target, Until = until };
                _speakers[id] = s;
            }
            else if (s!.Kind == u.Kind && s.Target == target && s.Until == until)
            {
                s.Stamp = _stamp;
                continue;
            }
            else
            {
                s.Kind = u.Kind;
                s.Target = target;
                s.Until = until;
            }

            s!.Stamp = _stamp;
            // a speaker first seen after the first gather says it only while it is still going; a changed one is new whatever its end time
            bool say = u.Kind != UtteranceKind.UtterNone && announce && (!isNew || until > now);
            if (say)
            {
                inbound.Enqueue(new HrotUtterance(id, KindName(u.Kind), target, now));
                UtterancesSent++;
            }
        }

        Sweep(_speakers, _scratchIds);
    }

    private string KindName(UtteranceKind kind) => _kindNames[(byte)kind] ??= kind.ToString();

    private static int TargetOf(EntityRepository world, Entity target)
        => !target.IsNull && world.IsAlive(target) && world.HasComponent<NetworkIdentity>(target) ? world.GetComponentRO<NetworkIdentity>(target).Value : 0;

    // ---- reports ----------------------------------------------------------------------------------------------------

    private void GatherReports(EntityRepository world, InMemoryInbound inbound, bool announce, Func<int, bool> isReplica)
    {
        _reportHolders.Clear();
        foreach (var e in _reportQuery!)
        {
            _reportHolders.Add(IdOf(world, e));
        }

        foreach (var e in _reportQuery!)
        {
            if (isReplica(IdOf(world, e)))
            {
                continue;
            }

            ref readonly var h = ref world.GetComponentRO<CommandReportHistory>(e);
            ReadOnlySpan<CommandReportEntry> items = h.Items;
            int count = Math.Min(h.Count, items.Length);
            bool isNew = !_reports.TryGetValue(e.PackedValue, out var cursor);
            if (isNew)
            {
                cursor = new Cursor<ReportKey>();
                _reports[e.PackedValue] = cursor;
            }

            cursor!.Stamp = _stamp;
            int fresh = 0;
            if (!isNew && cursor.Has)
            {
                fresh = count;   // the newest seen is gone (more than a list's worth came): all of them are new
                for (int i = 0; i < count; i++)
                {
                    if (KeyOf(in items[i]).Equals(cursor.Newest))
                    {
                        fresh = i;
                        break;
                    }
                }
            }
            else if (!isNew)
            {
                fresh = count;   // there was nothing before
            }

            if (announce && !isNew)
            {
                for (int i = fresh - 1; i >= 0; i--)
                {
                    ref readonly var entry = ref items[i];
                    // one event per report: the sender's copy, or the addressee's when the sender keeps no history
                    if (entry.Traffic == CommandTraffic.Incoming && _reportHolders.Contains(entry.FromNetId))
                    {
                        continue;
                    }

                    ref readonly var r = ref entry.Report;
                    inbound.Enqueue(new HrotReport(entry.FromNetId, entry.ToNetId, (byte)r.Kind, r.Subtype, r.Count, (byte)r.Size, (byte)r.Activity, (byte)r.Equipment,
                        (byte)r.Strength, (byte)r.Ammo, r.Wia, r.Kia, r.Synthetic != 0, (double)entry.At));
                    ReportsSent++;
                }
            }

            cursor.Has = count > 0;
            if (count > 0)
            {
                cursor.Newest = KeyOf(in items[0]);
            }
        }

        Sweep(_reports, _scratch);
    }

    private static ReportKey KeyOf(in CommandReportEntry e)
        => new((double)e.At, e.FromNetId, e.ToNetId, e.Report.Seq, (byte)e.Traffic, (byte)e.Report.Kind, e.Report.Subtype);

    // ---- orders -----------------------------------------------------------------------------------------------------

    private void GatherOrders(EntityRepository world, InMemoryInbound inbound, bool announce, BehaviorRegistry? behaviors, Func<int, bool> isReplica)
    {
        _orderHolders.Clear();
        foreach (var e in _orderQuery!)
        {
            _orderHolders.Add(IdOf(world, e));
        }

        foreach (var e in _orderQuery!)
        {
            int holder = IdOf(world, e);
            if (isReplica(holder))
            {
                continue;
            }

            ref readonly var h = ref world.GetComponentRO<CommandOrderHistory>(e);
            ReadOnlySpan<CommandOrderEntry> items = h.Items;
            int count = Math.Min(h.Count, items.Length);
            bool isNew = !_orders.TryGetValue(e.PackedValue, out var cursor);
            if (isNew)
            {
                cursor = new Cursor<OrderKey>();
                _orders[e.PackedValue] = cursor;
            }

            cursor!.Stamp = _stamp;
            int fresh = count;
            if (!isNew && cursor.Has)
            {
                for (int i = 0; i < count; i++)
                {
                    if (KeyOf(in items[i]).Equals(cursor.Newest))
                    {
                        fresh = i;
                        break;
                    }
                }
            }

            if (announce && !isNew)
            {
                for (int i = fresh - 1; i >= 0; i--)
                {
                    ref readonly var entry = ref items[i];
                    // one event per order: the giver's copy, or the member's when the giver keeps no history
                    if (entry.Traffic == CommandTraffic.Incoming && _orderHolders.Contains(entry.CounterpartNetId))
                    {
                        continue;
                    }

                    if (behaviors is null || !behaviors.TryGetName(entry.Behaviour, out string? name) || string.IsNullOrEmpty(name))
                    {
                        continue;   // names, not numbers: a behaviour the registry does not know is not sent
                    }

                    bool outgoing = entry.Traffic == CommandTraffic.Outgoing;
                    inbound.Enqueue(new HrotOrder(outgoing ? holder : entry.CounterpartNetId, outgoing ? entry.CounterpartNetId : holder, name, (double)entry.At));
                    OrdersSent++;
                }
            }

            cursor.Has = count > 0;
            if (count > 0)
            {
                cursor.Newest = KeyOf(in items[0]);
            }
        }

        Sweep(_orders, _scratch);
    }

    private static OrderKey KeyOf(in CommandOrderEntry e) => new((double)e.At, e.CounterpartNetId, e.Behaviour, (byte)e.Traffic);

    // ---- helpers ----------------------------------------------------------------------------------------------------

    /// <summary>The id HROT's history stores for a unit: its network id, else its slot index (<c>CommandPictureOps.NetIdOf</c>).</summary>
    private static int IdOf(EntityRepository world, Entity e)
        => world.HasComponent<NetworkIdentity>(e) ? world.GetComponentRO<NetworkIdentity>(e).Value : e.Index;

    private void Sweep(Dictionary<int, Speaker> map, List<int> scratch)
    {
        scratch.Clear();
        foreach (var (id, s) in map)
        {
            if (s.Stamp != _stamp)
            {
                scratch.Add(id);
            }
        }

        foreach (int id in scratch)
        {
            map.Remove(id);
        }
    }

    private void Sweep<TKey>(Dictionary<ulong, Cursor<TKey>> map, List<ulong> scratch) where TKey : struct
    {
        scratch.Clear();
        foreach (var (id, c) in map)
        {
            if (c.Stamp != _stamp)
            {
                scratch.Add(id);
            }
        }

        foreach (ulong id in scratch)
        {
            map.Remove(id);
        }
    }
}
