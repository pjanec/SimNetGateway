using System.Diagnostics;
using System.Reflection;
using CycloneDDS.Runtime;
using CycloneDDS.Schema;
using SimNetGateway.Protocol.DdsPlayer;

namespace SimNetGateway.Protocol.DdsPlayer.Tests;

/// <summary>T-062 (DD-025): the EntityStatus topic - Discrete, keyed by the entity, transient-local with one sample kept, the symbol bounded at 40, the none-values and the mind band round trip, the IDL gains lines only.</summary>
public class DpsEntityStatusTests
{
    private static uint NewDomain() => (uint)(55 + (Environment.ProcessId % 20));

    private static T RoundTrip<T>(T value, string partition)
        where T : struct
    {
        using var participant = new DdsParticipant(NewDomain(), defaultPartition: partition);
        using var writer = new DdsWriter<T>(participant);
        using var reader = new DdsReader<T>(participant);
        Assert.True(writer.WaitForReaderAsync(TimeSpan.FromSeconds(5)).GetAwaiter().GetResult());
        writer.Write(value);
        T? got = null;
        var sw = Stopwatch.StartNew();
        while (got is null && sw.ElapsedMilliseconds < 5000)
        {
            using var loan = reader.Take(8);
            foreach (var sample in loan)
            {
                if (sample.IsValid)
                {
                    got = sample.Data;
                }
            }

            if (got is null)
            {
                Thread.Sleep(20);
            }
        }

        Assert.NotNull(got);
        return got!.Value;
    }

    [Fact]
    public void The_topic_is_discrete_keyed_by_the_entity_and_appended_to_the_topic_list()
    {
        Assert.Equal("DPS_EntityStatus", DpsProtocol.EntityStatus);
        Assert.Contains(DpsProtocol.EntityStatus, DpsProtocol.AllTopics);
        Assert.Equal(DpsRateClass.Discrete, typeof(EntityStatus).GetCustomAttribute<DpsRateAttribute>()!.RateClass);
        Assert.Equal(DpsProtocol.EntityStatus, typeof(EntityStatus).GetCustomAttribute<DdsTopicAttribute>()!.TopicName);
        Assert.Equal(["EntityId"], typeof(EntityStatus).GetFields().Where(f => f.GetCustomAttribute<DdsKeyAttribute>() is not null).Select(f => f.Name).ToArray());
        var qos = typeof(EntityStatus).GetCustomAttribute<DdsQosAttribute>()!;
        Assert.Equal((DdsDurability.TransientLocal, DdsHistoryKind.KeepLast, 1), (qos.Durability, qos.HistoryKind, qos.HistoryDepth));
    }

    [Fact]
    public void Every_field_round_trips_the_symbol_at_its_bound_and_the_none_values_stay()
    {
        var full = RoundTrip(new EntityStatus
        {
            EntityId = 2001, Symbol = new string('S', 40), HealthCurrent = 59.98f, HealthMax = 100f, StrengthPresent = 3, StrengthAuthorized = 4, AmmoShare = 0.4375f,
            MindFill = 0.62f, MindBreak = 0.8f, MindBand = MindBand.MindBandNervous, Behaviour = new string('B', 32), Task = "Patrol",
        }, "t62-full");
        Assert.Equal((2001, new string('S', 40), 59.98f, 100f, 3, 4, 0.4375f, 0.62f, 0.8f, MindBand.MindBandNervous),
            (full.EntityId, full.Symbol, full.HealthCurrent, full.HealthMax, full.StrengthPresent, full.StrengthAuthorized, full.AmmoShare, full.MindFill, full.MindBreak, full.MindBand));
        Assert.Equal((new string('B', 32), "Patrol"), (full.Behaviour, full.Task));   // T-063: appended, bounded at 32

        var none = RoundTrip(new EntityStatus { EntityId = 7, Symbol = string.Empty, AmmoShare = -1f, MindBreak = -1f }, "t62-none");
        Assert.Equal((7, string.Empty, 0f, 0f, 0, 0, -1f, 0f, -1f, MindBand.MindBandNone), (none.EntityId, none.Symbol, none.HealthCurrent, none.HealthMax, none.StrengthPresent, none.StrengthAuthorized, none.AmmoShare, none.MindFill, none.MindBreak, none.MindBand));
    }

    [Fact]
    public void The_mind_bands_are_the_five_of_the_design_and_none_is_zero()
    {
        Assert.Equal(["MindBandNone", "MindBandCalm", "MindBandNervous", "MindBandBroken", "MindBandFear"], Enum.GetNames<MindBand>());
        Assert.Equal(MindBand.MindBandNone, default);
    }

    [Fact]
    public void The_committed_idl_declares_the_topic_and_its_enumeration()
    {
        string root = AppContext.BaseDirectory;
        string dir = root;
        while (dir is not null && !Directory.Exists(Path.Combine(dir, "docs", "clients", "idl")))
        {
            dir = Path.GetDirectoryName(dir)!;
        }

        string topics = File.ReadAllText(Path.Combine(dir, "docs", "clients", "idl", "DpsTopics.idl"));
        string types = File.ReadAllText(Path.Combine(dir, "docs", "clients", "idl", "DpsTypes.idl"));
        Assert.Contains("struct EntityStatus {", topics);
        Assert.Contains("@key int32 EntityId;", topics[topics.IndexOf("struct EntityStatus {", StringComparison.Ordinal)..]);
        Assert.Contains("string<40> Symbol;", topics);
        string status = topics[topics.IndexOf("struct EntityStatus {", StringComparison.Ordinal)..];
        status = status[..status.IndexOf("};", StringComparison.Ordinal)];
        // T-063: Behaviour and Task are the LAST two members (appended: an older reader of the appendable type still reads the rest)
        Assert.EndsWith("MindBand;\n                string<32> Behaviour;\n                string<32> Task;\n            ", status.Replace("\r\n", "\n"));
        Assert.Contains("enum MindBand {", types);
    }
}
