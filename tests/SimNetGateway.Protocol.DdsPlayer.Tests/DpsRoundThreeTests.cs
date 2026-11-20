using System.Diagnostics;
using System.Reflection;
using CycloneDDS.Runtime;
using CycloneDDS.Schema;
using SimNetGateway.Protocol.DdsPlayer;

namespace SimNetGateway.Protocol.DdsPlayer.Tests;

/// <summary>DPS round 3 (DD-019): View, Impact, the appended fields, the event history depth and the committed IDL.</summary>
public class DpsRoundThreeTests
{
    // same domain range as DpsTopicTests (Protocol.DdsPlayer 55..94; Plugin 1..50, DpsClient 100..166; all below 167, see DpsTopicTests.NewDomain)
    private static uint NewDomain() => (uint)(55 + (Environment.ProcessId % 20));

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

    private static T RoundTrip<T>(T value, string partition)
        where T : struct
    {
        using var participant = new DdsParticipant(NewDomain(), defaultPartition: partition);
        using var writer = new DdsWriter<T>(participant);
        using var reader = new DdsReader<T>(participant);
        Assert.True(writer.WaitForReaderAsync(TimeSpan.FromSeconds(5)).GetAwaiter().GetResult());
        writer.Write(value);
        T? got = null;
        Assert.True(WaitFor(() =>
        {
            using var loan = reader.Take(8);
            foreach (var sample in loan)
            {
                if (sample.IsValid)
                {
                    got = sample.Data;
                }
            }

            return got is not null;
        }));
        return got!.Value;
    }

    [Fact]
    public void View_and_impact_round_trip_and_the_view_is_a_late_join_state_topic()
    {
        var view = new View
        {
            Source = ViewSource.ViewSourceDirector, Mode = ViewMode.ViewModeShoulder, TargetId = 12, Distance = 2.2f, Side = -1f, Position = new Vec3d { X = 1, Y = 2, Z = 3 },
            Orientation = new Quat { Z = 0.5f, W = 0.5f }, FovDeg = 60f, CutSeq = 7, Shot = "Wide of the bridge", SimTime = 42.5,
        };
        var got = RoundTrip(view, "t3-view");
        Assert.Equal((ViewSource.ViewSourceDirector, ViewMode.ViewModeShoulder, 12, 2.2f, -1f, 60f, 7u, "Wide of the bridge", 42.5),
            (got.Source, got.Mode, got.TargetId, got.Distance, got.Side, got.FovDeg, got.CutSeq, got.Shot, got.SimTime));
        Assert.Equal((1.0, 2.0, 3.0, 0.5f, 0.5f), (got.Position.X, got.Position.Y, got.Position.Z, got.Orientation.Z, got.Orientation.W));

        // one instance, TransientLocal: a reader that comes later still gets the newest sample
        using var writerSide = new DdsParticipant(NewDomain(), defaultPartition: "t3-view-late");
        using var writer = new DdsWriter<View>(writerSide);
        writer.Write(view);
        view.CutSeq = 8;
        writer.Write(view);
        using var readerSide = new DdsParticipant(NewDomain(), defaultPartition: "t3-view-late");
        using var reader = new DdsReader<View>(readerSide);
        uint seen = 0;
        Assert.True(WaitFor(() =>
        {
            using var loan = reader.Take(8);
            foreach (var sample in loan)
            {
                if (sample.IsValid)
                {
                    seen = sample.Data.CutSeq;
                }
            }

            return seen != 0;
        }));
        Assert.Equal(8u, seen);

        var impact = RoundTrip(new Impact
        {
            SimTime = 9.5, Position = new Vec3d { X = 10, Y = 20, Z = 0.1 }, Normal = new Vec3f { Z = 1f }, Surface = EffectSurface.EffectSurfaceGround, Kind = ImpactKind.ImpactKindStopped,
            MunitionTypeId = 5_000_000_077L, ShooterId = 31, Material = "Concrete",
        }, "t3-impact");
        Assert.Equal((9.5, EffectSurface.EffectSurfaceGround, ImpactKind.ImpactKindStopped, 5_000_000_077L, 31, "Concrete", 1f),
            (impact.SimTime, impact.Surface, impact.Kind, impact.MunitionTypeId, impact.ShooterId, impact.Material, impact.Normal.Z));
    }

    [Fact]
    public void Round_and_animal_carry_the_appended_fields()
    {
        var round = RoundTrip(new Round { EntityId = 5, MunitionTypeId = 9, LaunchedAt = 123.456, BurnSeconds = 5f, FuzeLeftS = -1f }, "t3-round");
        Assert.Equal((123.456, 5f), (round.LaunchedAt, round.BurnSeconds));
        var animal = RoundTrip(new Animal { EntityId = 6, FlockCount = 9, FlockMode = FlockMode.FlockModeFlying, Airborne = true }, "t3-animal");
        Assert.True(animal.Airborne);
    }

    [Fact]
    public void The_six_event_topics_keep_1024_samples_and_the_state_topics_keep_one()
    {
        foreach (var type in new[] { typeof(Fire), typeof(Detonation), typeof(Effect), typeof(Impact), typeof(ManDowned), typeof(PlayerMessage) })
        {
            Assert.Equal(1024, type.GetCustomAttribute<DdsQosAttribute>()!.HistoryDepth);
        }

        foreach (var type in new[] { typeof(View), typeof(Round), typeof(Animal), typeof(EntityState) })
        {
            Assert.Equal(1, type.GetCustomAttribute<DdsQosAttribute>()!.HistoryDepth);
        }
    }

    [Fact]
    public async Task A_burst_of_a_thousand_fire_events_is_kept_for_a_reader_that_has_not_taken_yet()
    {
        // a minigun raises 50 - 100 fire events a second: a reader that takes once a frame must not lose the burst (depth 16 kept 16)
        using var participant = new DdsParticipant(NewDomain(), defaultPartition: "t3-burst");
        using var writer = new DdsWriter<Fire>(participant);
        using var reader = new DdsReader<Fire>(participant);
        Assert.True(await writer.WaitForReaderAsync(TimeSpan.FromSeconds(5)));
        for (int i = 1; i <= 1000; i++)
        {
            writer.Write(new Fire { SimTime = i, ShooterId = 1 });
        }

        var times = new List<double>();
        WaitFor(() =>
        {
            using var loan = reader.Take(2048);
            foreach (var sample in loan)
            {
                if (sample.IsValid)
                {
                    times.Add(sample.Data.SimTime);
                }
            }

            return times.Count >= 1000;
        });
        Assert.Equal(1000, times.Count);
        Assert.Equal(1.0, times[0]);
        Assert.Equal(1000.0, times[^1]);
    }

    // ---- the committed IDL (DD-019 3.6) -------------------------------------------------------------------------------------------

    private static string RepositoryRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "SimNetGateway.sln")))
        {
            dir = dir.Parent;
        }

        return dir?.FullName ?? throw new InvalidOperationException("SimNetGateway.sln not found above " + AppContext.BaseDirectory);
    }

    /// <summary>The IDL text without the header line of the committed copy and the generator's timestamp, line endings normalized.</summary>
    internal static string Normalize(string idl) =>
        string.Join('\n', idl.Replace("\r\n", "\n").Split('\n').Where(l => !l.StartsWith("// DPS protocol ", StringComparison.Ordinal) && !l.StartsWith("// Generated on:", StringComparison.Ordinal))).Trim();

    [Theory]
    [InlineData("DpsTopics.idl")]
    [InlineData("DpsTypes.idl")]
    public void The_committed_idl_equals_the_generated_one(string file)
    {
        string committedPath = Path.Combine(RepositoryRoot(), "docs", "clients", "idl", file);
        Assert.True(File.Exists(committedPath), $"{committedPath} is missing: run  bash scripts/refresh-dps-idl.sh  and commit docs/clients/idl/");
        string committed = File.ReadAllText(committedPath);
        string generated = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, file));

        Assert.True(Normalize(committed) == Normalize(generated),
            $"docs/clients/idl/{file} differs from the IDL generated from SimNetGateway.Protocol.DdsPlayer. The contract changed: run  bash scripts/refresh-dps-idl.sh  and commit docs/clients/idl/.");
        Assert.Equal($"// DPS protocol {DpsProtocol.Major}.{DpsProtocol.Minor}, generated from SimNetGateway.Protocol.DdsPlayer - do not edit", committed.Replace("\r\n", "\n").Split('\n')[0]);
    }

    [Fact]
    public void The_idl_comparison_ignores_the_header_the_timestamp_and_line_endings_but_not_content()
    {
        const string a = "// DPS protocol 1.0, generated from X - do not edit\n// Auto-generated\nstruct A { int x; };\n";
        const string b = "// Auto-generated\r\n// Generated on: 2026-01-01 00:00:00 UTC\r\nstruct A { int x; };\r\n";
        const string c = "// Auto-generated\nstruct A { int y; };\n";
        Assert.Equal(Normalize(a), Normalize(b));
        Assert.NotEqual(Normalize(a), Normalize(c));
    }
}
