using System.Diagnostics;
using System.Reflection;
using System.Text.RegularExpressions;
using CycloneDDS.Runtime;
using CycloneDDS.Schema;
using SimNetGateway.Protocol.DdsPlayer;

namespace SimNetGateway.Protocol.DdsPlayer.Tests;

/// <summary>DPS round 4 (DD-020): rate classes, PersonAim, the appended intent and enum fields, a contract that names no producer.</summary>
public class DpsRoundFourTests
{
    // same domain range as DpsTopicTests (Protocol.DdsPlayer 55..94; Plugin 1..50, DpsClient 100..166; all below 167, see DpsTopicTests.NewDomain)
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

    private static Dictionary<string, DpsRateClass> RateOfTopics()
    {
        var map = new Dictionary<string, DpsRateClass>();
        foreach (var type in typeof(DpsProtocol).Assembly.GetTypes())
        {
            if (type.GetCustomAttribute<DdsTopicAttribute>() is { } topic && type.GetCustomAttribute<DpsRateAttribute>() is { } rate)
            {
                map[topic.TopicName ?? string.Empty] = rate.RateClass;
            }
        }

        return map;
    }

    [Fact]
    public void Every_topic_states_its_rate_class()
    {
        var rates = RateOfTopics();
        foreach (string topic in DpsProtocol.AllTopics)
        {
            Assert.True(rates.ContainsKey(topic), $"{topic} has no [DpsRate] class");
        }

        Assert.Equal(DpsProtocol.AllTopics.Count, rates.Count);
        Assert.Equal(DpsRateClass.Continuous, rates[DpsProtocol.PersonAim]);
        Assert.Equal(DpsRateClass.Discrete, rates[DpsProtocol.PersonState]);
        Assert.Equal(DpsRateClass.Event, rates[DpsProtocol.Fire]);
        Assert.Equal(DpsRateClass.Fixed, rates[DpsProtocol.Entity]);
    }

    [Fact]
    public void PersonAim_and_the_intent_fields_round_trip()
    {
        var aim = RoundTrip(new PersonAim
        {
            EntityId = 5, SimTime = 7.5, AimTargetId = 77, AimPoint = new Vec3d { X = 1, Y = 2, Z = 3 }, LookTargetId = 78, HasLookTarget = true,
            LookTarget = new Vec3d { X = 4, Y = 5, Z = 6 }, HeadYaw = 0.5f,
        }, "t4-aim");
        Assert.Equal((5, 7.5, 77, 78, true, 0.5f, 3.0, 6.0), (aim.EntityId, aim.SimTime, aim.AimTargetId, aim.LookTargetId, aim.HasLookTarget, aim.HeadYaw, aim.AimPoint.Z, aim.LookTarget.Z));

        var person = RoundTrip(new PersonState { EntityId = 5, StanceChangedAt = 12.4, StanceSeconds = 0.9f }, "t4-person");
        Assert.Equal((12.4, 0.9f), (person.StanceChangedAt, person.StanceSeconds));

        var look = RoundTrip(new VehicleLook
        {
            EntityId = 9, Parts = [], Openings = [new OpeningLook { OpeningId = 2, TargetProgress = 1f, ChangedAt = 4.5, Seconds = 1.5f }],
            RotorTarget = 1f, RotorFrom = 0.6f, RotorChangedAt = 3.25, RotorSpoolSeconds = 20f,
        }, "t4-look");
        Assert.Equal((1f, 0.6f, 3.25, 20f), (look.RotorTarget, look.RotorFrom, look.RotorChangedAt, look.RotorSpoolSeconds));
        Assert.Equal((1f, 4.5, 1.5f), (look.Openings[0].TargetProgress, look.Openings[0].ChangedAt, look.Openings[0].Seconds));
    }

    [Fact]
    public void The_producer_neutral_enums_round_trip()
    {
        var session = RoundTrip(new Session { Terrain = string.Empty, StationName = string.Empty, Vocabulary = "hrot/1", TerrainFormat = "hrot-terrain/1" }, "t4-session");
        Assert.Equal(("hrot/1", "hrot-terrain/1"), (session.Vocabulary, session.TerrainFormat));

        var art = RoundTrip(new Articulation { EntityId = 1, PartId = 2001, Kind = "Turret", PartKind = ArticulationKind.ArticulationKindTurret, Index = 1 }, "t4-art");
        Assert.Equal((ArticulationKind.ArticulationKindTurret, 1), (art.PartKind, art.Index));

        var frame = RoundTrip(new ControlFrame
        {
            Seq = 1, GaitMode = Gait.GaitWalk, StanceWanted = StanceRequest.StanceRequestProne, Verb = UseVerb.UseVerbOpenDoor, Gait = 1, Stance = 3, UseVerb = 1, ClientId = 9,
        }, "t4-frame");
        Assert.Equal((Gait.GaitWalk, StanceRequest.StanceRequestProne, UseVerb.UseVerbOpenDoor), (frame.GaitMode, frame.StanceWanted, frame.Verb));

        var downed = RoundTrip(new ManDowned { VictimId = 3, Part = BodyPart.BodyPartHead, BodyPart = 6 }, "t4-downed");
        Assert.Equal(BodyPart.BodyPartHead, downed.Part);

        var own = RoundTrip(new OwnUnit { UnitId = 3, Ammo = [], Verb = UseVerb.UseVerbDrive, UseVerb = 9, UseLabel = string.Empty }, "t4-own");
        Assert.Equal(UseVerb.UseVerbDrive, own.Verb);
    }

    // ---- a contract that names no producer (DD-020 3.3) ----------------------------------------------------------------------

    private static string RepositoryRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "SimNetGateway.sln")))
        {
            dir = dir.Parent;
        }

        return dir?.FullName ?? throw new InvalidOperationException("SimNetGateway.sln not found above " + AppContext.BaseDirectory);
    }

    /// <summary>The producer's name in the contract source may appear only in an "e.g." example of a vocabulary name.</summary>
    internal static List<string> LinesNamingTheProducer(IEnumerable<(string File, string[] Lines)> sources) =>
        [.. sources.SelectMany(s => s.Lines.Select((line, i) => (s.File, Line: line, No: i + 1)))
            .Where(l => Regex.IsMatch(l.Line, "hrot", RegexOptions.IgnoreCase) && !l.Line.Contains("e.g.", StringComparison.Ordinal))
            .Select(l => $"{l.File}:{l.No}: {l.Line.Trim()}")];

    [Fact]
    public void The_contract_source_does_not_mention_the_producer_outside_examples()
    {
        string dir = Path.Combine(RepositoryRoot(), "src", "Protocols", "SimNetGateway.Protocol.DdsPlayer");
        var sources = Directory.GetFiles(dir, "*.cs").Select(f => (Path.GetFileName(f), File.ReadAllLines(f))).ToList();
        Assert.Equal(3, sources.Count);
        Assert.Empty(LinesNamingTheProducer(sources));
    }

    [Fact]
    public void The_producer_check_flags_a_mention_and_allows_an_example()
    {
        Assert.Single(LinesNamingTheProducer([("x.cs", ["/// <summary>HROT's seat pose</summary>"])]));
        Assert.Empty(LinesNamingTheProducer([("x.cs", ["/// <summary>A name from a vocabulary (e.g. \"hrot/1\").</summary>"])]));
    }
}
