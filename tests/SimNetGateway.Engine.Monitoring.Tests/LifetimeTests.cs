using System.Collections;
using System.Reflection;
using System.Text;
using SimNetGateway.Engine;

namespace SimNetGateway.Engine.Monitoring.Tests;

/// <summary>Reflection helpers: a canonical text of everything a model shows, and a walk over everything it reaches.</summary>
internal static class Reflect
{
    /// <summary>The public state of an object (properties, fields, elements of sequences), depth first, as text.</summary>
    public static string Dump(object? value)
    {
        var sb = new StringBuilder();
        Dump(value, sb, 0);
        return sb.ToString();
    }

    private static void Dump(object? value, StringBuilder sb, int depth)
    {
        if (depth > 12)
        {
            sb.Append("<deep>");
            return;
        }

        switch (value)
        {
            case null:
                sb.Append("null");
                return;
            case string s:
                sb.Append('"').Append(s).Append('"');
                return;
            case AttrValue v:
                sb.Append(v.ToString());
                return;
            case IEnumerable seq:
                sb.Append('[');
                foreach (var item in seq)
                {
                    Dump(item, sb, depth + 1);
                    sb.Append(',');
                }

                sb.Append(']');
                return;
        }

        var type = value.GetType();
        if (type.IsPrimitive || type.IsEnum || value is decimal || value is Type)
        {
            sb.Append(Convert.ToString(value, System.Globalization.CultureInfo.InvariantCulture));
            return;
        }

        sb.Append(type.Name).Append('{');
        foreach (var p in type.GetProperties(BindingFlags.Public | BindingFlags.Instance).Where(p => p.GetIndexParameters().Length == 0))
        {
            sb.Append(p.Name).Append('=');
            Dump(p.GetValue(value), sb, depth + 1);
            sb.Append(';');
        }

        sb.Append('}');
    }

    /// <summary>Types the models must never hold: engine records that are recycled, triggers and transactions.</summary>
    public static readonly Type[] Forbidden =
    [
        typeof(EntityRecord), typeof(EntityDelta), typeof(ChangeSetRecord), typeof(SimEvent), typeof(AttributeBag),
        typeof(EntityTrigger), typeof(EventTrigger), typeof(SessionTrigger), typeof(SessionRequestTrigger),
        typeof(ChangeSetTrigger), typeof(TimerTrigger), typeof(ChangeSet),
    ];

    /// <summary>
    /// Everything reachable from the object through instance fields (arrays and collections included) that is of a forbidden
    /// type. The walk does not enter the engine's own reference types (the engine, its registries and repository): a model may
    /// hold the engine, but must not hold what the engine recycles.
    /// </summary>
    public static List<string> FindForbidden(object root)
    {
        var found = new List<string>();
        var seen = new HashSet<object>(ReferenceEqualityComparer.Instance);
        Walk(root, root.GetType().Name, seen, found, 0);
        return found;
    }

    private static void Walk(object? value, string path, HashSet<object> seen, List<string> found, int depth)
    {
        if (value is null || depth > 40)
        {
            return;
        }

        var type = value.GetType();
        if (type.IsPrimitive || type.IsEnum || value is string || value is Type || value is Delegate || value is MemberInfo)
        {
            return;
        }

        if (!type.IsValueType && !seen.Add(value))
        {
            return;
        }

        if (Forbidden.Any(f => f.IsAssignableFrom(type)))
        {
            found.Add(path + " : " + type.Name);
            return;
        }

        if (!type.IsValueType && !type.IsArray && type.Assembly == typeof(GatewayEngine).Assembly)
        {
            return; // the engine's own objects (engine, registries, rules): referencing them is allowed, entering them is not our business
        }

        if (value is Array array)
        {
            int i = 0;
            foreach (var item in array)
            {
                Walk(item, path + "[" + i++ + "]", seen, found, depth + 1);
            }

            return;
        }

        for (var t = type; t is not null && t != typeof(object); t = t.BaseType)
        {
            foreach (var field in t.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly))
            {
                if (field.FieldType.IsPrimitive || field.FieldType == typeof(string))
                {
                    continue;
                }

                Walk(field.GetValue(value), path + "." + field.Name, seen, found, depth + 1);
            }
        }
    }
}

/// <summary>
/// Models stay intact when the engine recycles what they copied (R-038, R-070, R-075): with the journal and the event log off or
/// tiny, models refreshed earlier still show exactly what they showed, and they hold no record, delta, trigger or entity record.
/// </summary>
public class LifetimeTests
{
    private static readonly (int Journal, int Events)[] Capacities = [(0, 0), (1, 1), (3, 2), (256, 64)];

    public static TheoryData<int, int> CapacityData()
    {
        var data = new TheoryData<int, int>();
        foreach (var (journal, events) in Capacities)
        {
            data.Add(journal, events);
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(CapacityData))]
    public void Models_refreshed_earlier_still_show_their_copies_after_many_further_ticks(int journal, int events)
    {
        var world = new World(new GatewayOptions { JournalCapacity = journal, EventLogCapacity = events, DiagnosticsCapacity = 32 }, 17);
        var options = new EntityTableOptions { NameAttribute = world.Name, ParentAttribute = world.Parent };
        var models = new AllModels(world.Engine, options, world.Mirror, 17, null, maxRecords: 100, maxWrites: 100);
        world.Run(150);
        for (int i = 0; i < 12; i++)
        {
            world.RandomTick();
            models.RefreshAndCheck("warmup " + i); // also selects an entity and gives every model content
        }

        for (int guard = 0; world.Engine.State.Count <= 8 && guard < 500; guard++)
        {
            world.RandomTick();
        }

        // pick an entity that has a history and a parent link, so the inspector has something of each kind
        var withMembers = world.Live.FirstOrDefault(id => world.Engine.State.Query().ReferencedBy(world.Parent, id).Count() > 0);
        models.Inspector.Load(withMembers.IsNone ? world.Live[0] : withMembers);
        models.RefreshAndCheck("final");
        models.Table.Filter = new EntityFilter();
        models.Table.Sort(EntityColumn.Name);
        models.Table.Refresh();
        Assert.True(world.Engine.State.Count > 5);

        var all = new object[] { models.Table, models.Inspector, models.Rules, models.Journal, models.Identities, models.Diagnostics, models.Vocabulary };
        var before = all.Select(Reflect.Dump).ToArray();
        Assert.True(before.All(b => b.Length > 20));
        Assert.True(models.Table.Rows.Count > 5);
        Assert.True(models.Journal.Events.Count > 0 || events == 0);

        world.Run(500); // the engine recycles its records, events and diagnostics many times over

        for (int i = 0; i < all.Length; i++)
        {
            Assert.True(before[i] == Reflect.Dump(all[i]), $"the {all[i].GetType().Name} changed without a refresh (journal {journal}, events {events})");
        }

        // and they hold nothing the engine recycles
        foreach (var model in all)
        {
            Assert.True(Reflect.FindForbidden(model).Count == 0, model.GetType().Name + " reaches " + string.Join(", ", Reflect.FindForbidden(model)));
        }
    }

    [Fact]
    public void The_tracer_and_its_traces_hold_no_engine_records()
    {
        var tracer = new DispatchTracer(500) { WatchAll = true, IncludeTimers = true };
        var world = new World(new GatewayOptions { JournalCapacity = 2, EventLogCapacity = 2 }, 19, tracer);
        world.Run(200);
        Assert.True(tracer.Traces.Count > 20);

        world.Run(200);

        Assert.True(Reflect.FindForbidden(tracer).Count == 0, string.Join(", ", Reflect.FindForbidden(tracer)));

        // a trace a reader holds never changes after it entered the ring
        var held = tracer.Traces.Take(5).ToList();
        var dumps = held.Select(Reflect.Dump).ToList();
        world.Run(100);
        Assert.True(dumps.SequenceEqual(held.Select(Reflect.Dump)));
    }

    [Fact]
    public void The_walker_finds_what_it_is_meant_to_forbid()
    {
        var world = new World(seed: 3);
        world.Run(60);
        Assert.NotEmpty(world.Live);
        var entity = world.Engine.State.Find(world.Live[0])!;
        var holder = new List<object> { new { Nested = new[] { entity } } };

        Assert.Single(Reflect.FindForbidden(entity));
        Assert.Single(Reflect.FindForbidden(new[] { entity }));
        Assert.Single(Reflect.FindForbidden(new { Nested = new[] { entity } }));
        Assert.Single(Reflect.FindForbidden(holder));
        Assert.Empty(Reflect.FindForbidden(new List<object> { "text", 5, world.Engine.Rules }));
    }
}
