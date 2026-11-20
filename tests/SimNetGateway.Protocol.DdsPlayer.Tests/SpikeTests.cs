using System.Diagnostics;
using CycloneDDS.Runtime;
using CycloneDDS.Schema;

namespace SimNetGateway.Protocol.DdsPlayer.Tests;

[DdsTopic("SpikeState")]
[DdsQos(Reliability = DdsReliability.Reliable, Durability = DdsDurability.TransientLocal,
        HistoryKind = DdsHistoryKind.KeepLast, HistoryDepth = 1)]
public partial struct SpikeState
{
    [DdsKey, DdsId(0)] public int Id;
    [DdsId(1)] public int Value;
}

[DdsTopic("SpikeSilent")]
[DdsQos(Reliability = DdsReliability.Reliable, LivelinessKind = DdsLivelinessKind.ManualByTopic, LivelinessLeaseDurationMs = 300)]
public partial struct SpikeSilent
{
    [DdsKey, DdsId(0)] public int Id;
    [DdsId(1)] public int Seq;
}

[DdsTopic("SpikePlayer")]
[DdsQos(Reliability = DdsReliability.Reliable, LivelinessKind = DdsLivelinessKind.Automatic, LivelinessLeaseDurationMs = 500)]
public partial struct SpikePlayer
{
    [DdsKey, DdsId(0)] public int Id;
    [DdsId(1)] public int Seq;
}

/// <summary>
/// Spike of DD-017 section 10: what CycloneDDS.NET 1.2.20 exposes for partitions, liveliness and dispose.
/// Kept as regression tests of the features DPS relies on.
/// </summary>
public class SpikeTests
{
    // DDS domains of the test assemblies are disjoint (Plugin 1..50, Protocol.DdsPlayer 55..94, DpsClient 100..166): the projects run in parallel and a shared domain lets their participants meet; all stay below domain 167 because DDS uses UDP port 7400 + 250 * domain and Windows excludes or hands out the ports above 49152, where participant creation then fails at random
    private static uint NewDomain() => (uint)(75 + (Environment.ProcessId % 20));

    private static bool WaitFor(Func<bool> condition, int timeoutMs = 5000)
    {
        var sw = Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < timeoutMs)
        {
            if (condition())
            {
                return true;
            }
            Thread.Sleep(20);
        }
        return condition();
    }

    private static int CountValid<T>(DdsReader<T> reader) where T : struct
    {
        using var loan = reader.Take(32);
        var n = 0;
        foreach (var sample in loan)
        {
            if (sample.IsValid)
            {
                n++;
            }
        }
        return n;
    }

    [Fact]
    public async Task Partition_isolates_two_stations()
    {
        var domain = NewDomain();
        using var pa = new DdsParticipant(domain, defaultPartition: "stationA");
        using var pb = new DdsParticipant(domain, defaultPartition: "stationB");
        using var writerA = new DdsWriter<SpikeState>(pa);
        using var readerA = new DdsReader<SpikeState>(pa);
        using var readerB = new DdsReader<SpikeState>(pb);
        using var readerAExplicit = new DdsReader<SpikeState>(pb, null, partition: "stationA");

        Assert.True(await writerA.WaitForReaderAsync(TimeSpan.FromSeconds(5)));
        writerA.Write(new SpikeState { Id = 1, Value = 42 });

        var gotA = 0;
        var gotExplicit = 0;
        WaitFor(() => { gotA += CountValid(readerA); gotExplicit += CountValid(readerAExplicit); return gotA > 0 && gotExplicit > 0; });
        Thread.Sleep(300);
        var gotB = CountValid(readerB);

        Assert.Equal(1, gotA);
        Assert.Equal(1, gotExplicit);
        Assert.Equal(0, gotB);
    }

    [Fact]
    public void Lost_writer_is_reported()
    {
        var domain = NewDomain();
        using var gateway = new DdsParticipant(domain, defaultPartition: "stationA");
        using var reader = new DdsReader<SpikePlayer>(gateway);
        var alive = 0;
        var notAlive = 0;
        reader.LivelinessChanged += (_, st) =>
        {
            if (st.AliveCountChange > 0) { Interlocked.Add(ref alive, st.AliveCountChange); }
            if (st.AliveCountChange < 0 || st.NotAliveCountChange > 0) { Interlocked.Increment(ref notAlive); }
        };

        var client = new DdsParticipant(domain, defaultPartition: "stationA");
        var writer = new DdsWriter<SpikePlayer>(client);
        writer.Write(new SpikePlayer { Id = 1, Seq = 1 });
        Assert.True(WaitFor(() => Volatile.Read(ref alive) > 0), "writer should be reported alive");
        Assert.Equal(1u, reader.LivelinessChangedStatus.AliveCount);
        Assert.Equal(0, Volatile.Read(ref notAlive));

        // The client vanishes without a goodbye: no unregister, no dispose of the instance by the application.
        writer.Dispose();
        client.Dispose();

        Assert.True(WaitFor(() => Volatile.Read(ref notAlive) > 0, 8000), "writer should be reported lost");
        Assert.Equal(0u, reader.LivelinessChangedStatus.AliveCount);
    }

    [Fact]
    public void Silent_writer_lease_expires_while_the_writer_still_exists()
    {
        var domain = NewDomain();
        using var gateway = new DdsParticipant(domain, defaultPartition: "stationB");
        using var reader = new DdsReader<SpikeSilent>(gateway);
        var lost = 0;
        reader.LivelinessChanged += (_, st) =>
        {
            if (st.AliveCountChange < 0 || st.NotAliveCountChange > 0) { Interlocked.Increment(ref lost); }
        };

        using var client = new DdsParticipant(domain, defaultPartition: "stationB");
        using var writer = new DdsWriter<SpikeSilent>(client);
        writer.Write(new SpikeSilent { Id = 1, Seq = 1 });
        Assert.True(WaitFor(() => reader.LivelinessChangedStatus.AliveCount == 1), "alive after the write");

        // No further write and no assert: the 300 ms lease runs out although the writer object is alive.
        Assert.True(WaitFor(() => Volatile.Read(ref lost) > 0, 5000), "lease expiry should be reported");
        Assert.Equal(0u, reader.LivelinessChangedStatus.AliveCount);

        // Any new sample brings it back to alive.
        writer.Write(new SpikeSilent { Id = 1, Seq = 2 });
        Assert.True(WaitFor(() => reader.LivelinessChangedStatus.AliveCount == 1), "alive again after a write");
    }

    [Fact]
    public void Disposed_instance_is_not_delivered_to_late_joiner()
    {
        var domain = NewDomain();
        using var pw = new DdsParticipant(domain, defaultPartition: "stationA");
        using var writer = new DdsWriter<SpikeState>(pw);
        writer.Write(new SpikeState { Id = 1, Value = 10 });
        writer.Write(new SpikeState { Id = 2, Value = 20 });
        writer.DisposeInstance(new SpikeState { Id = 1 });

        using var pl = new DdsParticipant(domain, defaultPartition: "stationA");
        using var late = new DdsReader<SpikeState>(pl);
        Assert.True(WaitFor(() => late.LookupInstance(new SpikeState { Id = 2 }).IsNil == false));
        Thread.Sleep(300);

        var valid = new List<int>();
        var notAlive = new List<int>();
        var states = new List<string>();
        using (var loan = late.Take(32))
        {
            foreach (var s in loan)
            {
                if (s.IsValid) { valid.Add(s.Data.Id); }
                else { notAlive.Add(s.Data.Id); states.Add(s.Info.InstanceState.ToString()); }
            }
        }

        Assert.Equal(new[] { 2 }, valid);
        Assert.DoesNotContain(1, valid);
        // The late joiner is told about the disposed instance as a not-alive sample without data (the client treats it as "entity gone").
        Assert.Equal(new[] { 1 }, notAlive);
        Assert.Contains("Disposed", Assert.Single(states), StringComparison.OrdinalIgnoreCase);
    }
}
