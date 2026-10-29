using System.Text.Json;
using System.Text.Json.Nodes;
using SimNetGateway.Gateway.HrotDis;

namespace SimNetGateway.Gateway.HrotDis.Tests;

/// <summary>The configuration file of R-094 / DD-007 section 3a: every rule of the table, defaults, round trip, corruption.</summary>
public class ConfigurationTests
{
    /// <summary>The example of DD-007 section 3a.</summary>
    public const string Example = """
        {
          "shared": { "site": 1, "application": 3001, "entityMaxHealth": 100,
                      "damageThresholds": { "slight": 0.5, "moderate": 0.0 },
                      "defaultTkbForUnknown": 0, "forwardUnitEntities": false, "sendEventsInReplay": false },
          "hrot":   { "sampleRateHz": 20, "replicaOwnerNodeId": 65000, "allowZeroOrigin": false },
          "dis":    { "enabled": true, "exerciseId": 1, "port": 3000, "address": "255.255.255.255",
                      "positionThresholdM": 1.0, "orientationThresholdDeg": 3.0, "heartbeatS": 5, "timeoutS": 12,
                      "hrotControlsDis": true, "disControlsHrot": false },
          "hla":    { "enabled": false, "rti": "fake", "federation": "RPR", "federateName": "HROT-Gateway",
                      "federateType": "SimNetGateway", "fomModules": ["RPR-FOM-2.0"], "createFederation": true,
                      "positionThresholdM": 1.0, "orientationThresholdDeg": 3.0, "heartbeatS": 5, "timeoutS": 12 }
        }
        """;

    private static GatewayConfiguration Load(string json)
    {
        var result = GatewayConfigurationFile.Parse(json, "test.json");
        Assert.True(result.Kind == ConfigurationLoadKind.Loaded, string.Join("; ", result.Errors));
        return result.Configuration!;
    }

    private static ConfigurationLoadResult Invalid(string json)
    {
        var result = GatewayConfigurationFile.Parse(json, "test.json");
        Assert.Equal(ConfigurationLoadKind.Invalid, result.Kind);
        Assert.Null(result.Configuration);
        Assert.NotEmpty(result.Errors);
        return result;
    }

    [Fact]
    public void The_example_of_the_design_loads_with_its_values()
    {
        var c = Load(Example);
        Assert.Equal(1, c.Shared.Site);
        Assert.Equal(3001, c.Shared.Application);
        Assert.Equal(100, c.Shared.EntityMaxHealth);
        Assert.Equal(0.5, c.Shared.DamageThresholds.Slight);
        Assert.Equal(0.0, c.Shared.DamageThresholds.Moderate);
        Assert.Equal(0, c.Shared.DefaultTkbForUnknown);
        Assert.False(c.Shared.ForwardUnitEntities);
        Assert.False(c.Shared.SendEventsInReplay);
        Assert.Equal(20, c.Hrot.SampleRateHz);
        Assert.Equal(65000, c.Hrot.ReplicaOwnerNodeId);
        Assert.False(c.Hrot.AllowZeroOrigin);
        var dis = c.Dis!;
        Assert.True(dis.Enabled);
        Assert.Equal(1, dis.ExerciseId);
        Assert.Equal(3000, dis.Port);
        Assert.Equal("255.255.255.255", dis.Address);
        Assert.Equal(1.0, dis.PositionThresholdM);
        Assert.Equal(3.0, dis.OrientationThresholdDeg);
        Assert.Equal(5, dis.HeartbeatS);
        Assert.Equal(12, dis.TimeoutS);
        Assert.True(dis.HrotControlsDis);
        Assert.False(dis.DisControlsHrot);
        var hla = c.Hla!;
        Assert.False(hla.Enabled);
        Assert.Equal("fake", hla.Rti);
        Assert.Equal("RPR", hla.Federation);
        Assert.Equal("HROT-Gateway", hla.FederateName);
        Assert.Equal("SimNetGateway", hla.FederateType);
        Assert.Equal(["RPR-FOM-2.0"], hla.FomModules);
        Assert.True(hla.CreateFederation);
        Assert.Equal(1.0, hla.PositionThresholdM);
        Assert.Equal(3.0, hla.OrientationThresholdDeg);
        Assert.Equal(5, hla.HeartbeatS);
        Assert.Equal(12, hla.TimeoutS);
    }

    [Fact]
    public void Missing_optional_keys_take_the_documented_defaults()
    {
        var full = Load(Example);
        var minimal = Load("""{ "dis": { "exerciseId": 1 }, "hla": { "federation": "RPR" } }""");
        Assert.Equal(full.Shared, minimal.Shared);
        Assert.Equal(full.Hrot, minimal.Hrot);
        Assert.Equal(full.Dis, minimal.Dis);
        // hla.enabled: the example writes false explicitly; a present section without the key runs (DD-007 3a "enabled is not false").
        Assert.Equal(full.Hla! with { Enabled = true }, minimal.Hla);
        Assert.Equal(full.Shared, new SharedConfiguration());
        Assert.Equal(full.Hrot, new HrotConfiguration());
    }

    [Fact]
    public void The_hla_thresholds_are_their_own_keys_and_independent_of_the_dis_section()
    {
        var c = Load("""
            { "dis": { "exerciseId": 1, "positionThresholdM": 7, "orientationThresholdDeg": 9 },
              "hla": { "federation": "F", "positionThresholdM": 2.5, "orientationThresholdDeg": 4.5 } }
            """);
        Assert.Equal(7, c.Dis!.PositionThresholdM);
        Assert.Equal(9, c.Dis.OrientationThresholdDeg);
        Assert.Equal(2.5, c.Hla!.PositionThresholdM);
        Assert.Equal(4.5, c.Hla.OrientationThresholdDeg);
        Assert.Equal(c, Load(GatewayConfigurationFile.ToJson(c)));
        // not given: the hla defaults (1 m, 3 deg), whatever the dis section says
        var d = Load("""{ "dis": { "exerciseId": 1, "positionThresholdM": 7, "orientationThresholdDeg": 9 }, "hla": { "federation": "F" } }""");
        Assert.Equal(1.0, d.Hla!.PositionThresholdM);
        Assert.Equal(3.0, d.Hla.OrientationThresholdDeg);
    }

    [Fact]
    public void An_empty_object_is_valid_and_runs_no_side()
    {
        var c = Load("{}");
        Assert.Null(c.Dis);
        Assert.Null(c.Hla);
        Assert.False(c.AnySideEnabled);
        Assert.Equal(new SharedConfiguration(), c.Shared);
    }

    [Fact]
    public void A_side_runs_when_its_section_is_present_and_enabled_is_not_false()
    {
        Assert.True(Load("""{ "dis": { "exerciseId": 1 } }""").AnySideEnabled);
        Assert.True(Load("""{ "dis": { "exerciseId": 1, "enabled": true } }""").DisRuns);
        Assert.False(Load("""{ "dis": { "exerciseId": 1, "enabled": false } }""").AnySideEnabled);
        Assert.True(Load("""{ "hla": { "federation": "F" } }""").HlaRuns);
        Assert.False(Load("""{ "hla": { "federation": "F", "enabled": false } }""").AnySideEnabled);
        var both = Load("""{ "dis": { "exerciseId": 1, "enabled": false }, "hla": { "federation": "F" } }""");
        Assert.False(both.DisRuns);
        Assert.True(both.HlaRuns);
        Assert.True(both.AnySideEnabled);
        Assert.False(Load("""{ "shared": {}, "hrot": {} }""").AnySideEnabled);
    }

    [Fact]
    public void A_full_example_round_trips_through_json()
    {
        var c = Load(Example);
        var again = Load(GatewayConfigurationFile.ToJson(c));
        Assert.Equal(c, again);

        var changed = c with
        {
            Shared = new SharedConfiguration { Site = 7, Application = 9, EntityMaxHealth = 250.5, DefaultTkbForUnknown = 12,
                ForwardUnitEntities = true, SendEventsInReplay = true, DamageThresholds = new DamageThresholds { Slight = 0.8, Moderate = 0.1 } },
            Hrot = new HrotConfiguration { SampleRateHz = 7.5, ReplicaOwnerNodeId = 123, AllowZeroOrigin = true },
            Dis = c.Dis! with { ExerciseId = 200, Port = 65535, Address = "239.1.2.3", DisControlsHrot = true, HrotControlsDis = false },
            Hla = c.Hla! with { Enabled = true, FomModules = ["A.xml", "B.xml"], CreateFederation = false },
        };
        Assert.Equal(changed, Load(GatewayConfigurationFile.ToJson(changed)));
        Assert.Equal(GatewayConfiguration.CreateDefault(5), Load(GatewayConfigurationFile.ToJson(GatewayConfiguration.CreateDefault(5))));
    }

    // ---- invalid files ----------------------------------------------------------------------------------------------

    public static TheoryData<string, string> InvalidCases() => new()
    {
        // not JSON / wrong shape
        { "{ not json", "invalid JSON" },
        { "", "invalid JSON" },
        { "[]", "(top level)" },
        { "3", "(top level)" },
        { """{ "shared": 3 }""", "shared" },
        { """{ "dis": null }""", "dis" },
        { """{ "hla": [] }""", "hla" },
        // unknown keys at every level
        { """{ "unknown": 1 }""", "unknown" },
        { """{ "shared": { "colour": 1 } }""", "shared.colour" },
        { """{ "shared": { "damageThresholds": { "severe": 1 } } }""", "shared.damageThresholds.severe" },
        { """{ "hrot": { "sampleRate": 20 } }""", "hrot.sampleRate" },
        { """{ "dis": { "exerciseId": 1, "ports": 3 } }""", "dis.ports" },
        { """{ "hla": { "federation": "F", "extra": true } }""", "hla.extra" },
        { """{ "dis": { "exerciseId": 1, "ExerciseId": 2 } }""", "dis.ExerciseId" },
        // duplicate key
        { """{ "dis": { "exerciseId": 1, "exerciseId": 2 } }""", "dis.exerciseId" },
        // wrong types
        { """{ "shared": { "site": "1" } }""", "shared.site" },
        { """{ "shared": { "site": 1.5 } }""", "shared.site" },
        { """{ "shared": { "entityMaxHealth": "100" } }""", "shared.entityMaxHealth" },
        { """{ "shared": { "forwardUnitEntities": 1 } }""", "shared.forwardUnitEntities" },
        { """{ "shared": { "sendEventsInReplay": "no" } }""", "shared.sendEventsInReplay" },
        { """{ "shared": { "defaultTkbForUnknown": true } }""", "shared.defaultTkbForUnknown" },
        { """{ "hrot": { "allowZeroOrigin": "false" } }""", "hrot.allowZeroOrigin" },
        { """{ "hrot": { "sampleRateHz": null } }""", "hrot.sampleRateHz" },
        { """{ "dis": { "exerciseId": 1, "port": "3000" } }""", "dis.port" },
        { """{ "dis": { "exerciseId": 1, "address": 5 } }""", "dis.address" },
        { """{ "dis": { "exerciseId": "1" } }""", "dis.exerciseId" },
        { """{ "dis": { "exerciseId": 1, "enabled": "yes" } }""", "dis.enabled" },
        { """{ "hla": { "federation": 3 } }""", "hla.federation" },
        { """{ "hla": { "federation": "F", "fomModules": "RPR" } }""", "hla.fomModules" },
        { """{ "hla": { "federation": "F", "fomModules": [1] } }""", "hla.fomModules[0]" },
        // out of range
        { """{ "dis": { "exerciseId": 1, "port": 0 } }""", "dis.port" },
        { """{ "dis": { "exerciseId": 1, "port": 65536 } }""", "dis.port" },
        { """{ "dis": { "exerciseId": 1, "port": -3 } }""", "dis.port" },
        { """{ "dis": { "exerciseId": 256 } }""", "dis.exerciseId" },
        { """{ "dis": { "exerciseId": -1 } }""", "dis.exerciseId" },
        { """{ "dis": { "exerciseId": 1, "address": "not an address" } }""", "dis.address" },
        { """{ "dis": { "exerciseId": 1, "heartbeatS": 0 } }""", "dis.heartbeatS" },
        { """{ "dis": { "exerciseId": 1, "timeoutS": -1 } }""", "dis.timeoutS" },
        { """{ "dis": { "exerciseId": 1, "positionThresholdM": -0.5 } }""", "dis.positionThresholdM" },
        { """{ "dis": { "exerciseId": 1, "orientationThresholdDeg": -1 } }""", "dis.orientationThresholdDeg" },
        { """{ "hrot": { "sampleRateHz": 0 } }""", "hrot.sampleRateHz" },
        { """{ "hrot": { "sampleRateHz": -5 } }""", "hrot.sampleRateHz" },
        { """{ "hrot": { "replicaOwnerNodeId": -1 } }""", "hrot.replicaOwnerNodeId" },
        { """{ "shared": { "site": 65536 } }""", "shared.site" },
        { """{ "shared": { "application": -1 } }""", "shared.application" },
        { """{ "shared": { "entityMaxHealth": 0 } }""", "shared.entityMaxHealth" },
        { """{ "shared": { "defaultTkbForUnknown": -1 } }""", "shared.defaultTkbForUnknown" },
        { """{ "shared": { "damageThresholds": { "slight": -0.1 } } }""", "shared.damageThresholds.slight" },
        { """{ "shared": { "damageThresholds": { "moderate": -0.1 } } }""", "shared.damageThresholds.moderate" },
        { """{ "shared": { "damageThresholds": { "slight": 1.5 } } }""", "shared.damageThresholds.slight" },
        { """{ "shared": { "damageThresholds": { "slight": 0.2, "moderate": 0.3 } } }""", "shared.damageThresholds.moderate" },
        { """{ "hla": { "federation": "F", "positionThresholdM": -0.5 } }""", "hla.positionThresholdM" },
        { """{ "hla": { "federation": "F", "orientationThresholdDeg": -1 } }""", "hla.orientationThresholdDeg" },
        { """{ "hla": { "federation": "F", "heartbeatS": 0 } }""", "hla.heartbeatS" },
        { """{ "hla": { "federation": "F", "timeoutS": 0 } }""", "hla.timeoutS" },
        { """{ "hla": { "federation": "F", "fomModules": [] } }""", "hla.fomModules" },
        { """{ "hla": { "federation": "F", "rti": "" } }""", "hla.rti" },
        // required keys
        { """{ "dis": { "enabled": true } }""", "dis.exerciseId" },
        { """{ "hla": { "enabled": true } }""", "hla.federation" },
        { """{ "hla": { "federation": "" } }""", "hla.federation" },
    };

    [Theory]
    [MemberData(nameof(InvalidCases))]
    public void An_invalid_file_is_invalid_and_the_error_names_the_key_and_the_file(string json, string key)
    {
        var result = Invalid(json);
        Assert.Contains(result.Errors, e => e.Contains(key, StringComparison.Ordinal));
        Assert.All(result.Errors, e => Assert.StartsWith("test.json: ", e));
    }

    [Fact]
    public void Every_problem_of_a_file_is_reported_together()
    {
        var result = Invalid("""{ "dis": { "port": 0 }, "hrot": { "sampleRateHz": 0, "bogus": 1 }, "shared": { "site": "x" } }""");
        string all = string.Join("\n", result.Errors);
        Assert.Contains("dis.exerciseId", all);
        Assert.Contains("dis.port", all);
        Assert.Contains("hrot.sampleRateHz", all);
        Assert.Contains("hrot.bogus", all);
        Assert.Contains("shared.site", all);
    }

    [Fact]
    public void Random_single_key_corruptions_of_a_valid_file_are_invalid_and_name_the_key()
    {
        var random = new Random(20261029);
        var leaves = new List<string>();
        Collect(JsonNode.Parse(Example)!, "", leaves);
        Assert.Equal(32, leaves.Count); // every key of the example (32 since T-024: hla.positionThresholdM, hla.orientationThresholdDeg)

        int corrupted = 0;
        foreach (var path in leaves)
        {
            var root = JsonNode.Parse(Example)!;
            var parent = ParentOf(root, path, out string name);
            var original = parent[name]!;
            JsonNode replacement = WrongKind(original.GetValueKind(), random);
            parent[name] = replacement;
            AssertInvalidNaming(root.ToJsonString(), path);
            corrupted++;
        }

        // an unknown key next to any key of any object
        var objects = new List<string>();
        CollectObjects(JsonNode.Parse(Example)!, "", objects);
        foreach (var path in objects)
        {
            var root = JsonNode.Parse(Example)!;
            var obj = path.Length == 0 ? root.AsObject() : Navigate(root, path).AsObject();
            string unknown = "zz" + random.Next(1000);
            obj[unknown] = 1;
            AssertInvalidNaming(root.ToJsonString(), (path.Length == 0 ? "" : path + ".") + unknown);
            corrupted++;
        }

        // a required key that is gone
        foreach (var path in new[] { "dis.exerciseId", "hla.federation" })
        {
            var root = JsonNode.Parse(Example)!;
            var parent = ParentOf(root, path, out string name);
            parent.AsObject().Remove(name);
            AssertInvalidNaming(root.ToJsonString(), path);
            corrupted++;
        }

        Assert.Equal(32 + 6 + 2, corrupted); // every key, every object, both required keys
    }

    /// <summary>A value of a JSON kind that the key at hand does not accept.</summary>
    private static JsonNode WrongKind(JsonValueKind kind, Random random)
    {
        switch (kind)
        {
            case JsonValueKind.True:
            case JsonValueKind.False:
                return random.Next(2) == 0 ? JsonValue.Create("text") : JsonValue.Create(1);
            case JsonValueKind.Number:
                return random.Next(3) switch
                {
                    0 => JsonValue.Create("text"),
                    1 => JsonValue.Create(true),
                    _ => new JsonArray(),
                };
            case JsonValueKind.String:
                return random.Next(2) == 0 ? JsonValue.Create(5) : JsonValue.Create(false);
            default: // the fomModules array
                return random.Next(2) == 0 ? JsonValue.Create("RPR") : JsonValue.Create(7);
        }
    }

    private static void AssertInvalidNaming(string json, string key)
    {
        var result = GatewayConfigurationFile.Parse(json, "test.json");
        Assert.True(result.Kind == ConfigurationLoadKind.Invalid, $"corruption of {key} was accepted: {json}");
        Assert.Contains(result.Errors, e => e.Contains(key, StringComparison.Ordinal));
    }

    private static void Collect(JsonNode node, string path, List<string> leaves)
    {
        if (node is JsonObject obj)
        {
            foreach (var (name, child) in obj)
            {
                Collect(child!, path.Length == 0 ? name : path + "." + name, leaves);
            }
        }
        else
        {
            leaves.Add(path);
        }
    }

    private static void CollectObjects(JsonNode node, string path, List<string> objects)
    {
        if (node is JsonObject obj)
        {
            objects.Add(path);
            foreach (var (name, child) in obj)
            {
                CollectObjects(child!, path.Length == 0 ? name : path + "." + name, objects);
            }
        }
    }

    private static JsonNode Navigate(JsonNode root, string path)
    {
        var node = root;
        foreach (var part in path.Split('.'))
        {
            node = node[part]!;
        }

        return node;
    }

    private static JsonNode ParentOf(JsonNode root, string path, out string name)
    {
        int dot = path.LastIndexOf('.');
        name = path[(dot + 1)..];
        return dot < 0 ? root : Navigate(root, path[..dot]);
    }

    // ---- files ------------------------------------------------------------------------------------------------------

    private static string TempFile(string content)
    {
        string path = Path.Combine(Path.GetTempPath(), "simnetgateway-test-" + Guid.NewGuid().ToString("N") + ".json");
        File.WriteAllText(path, content);
        return path;
    }

    [Fact]
    public void A_missing_file_is_neither_loaded_nor_an_error()
    {
        string path = Path.Combine(Path.GetTempPath(), "simnetgateway-missing-" + Guid.NewGuid().ToString("N") + ".json");
        Assert.False(GatewayConfigurationFile.TryLoad(path, out var config, out var errors));
        Assert.Null(config);
        Assert.Empty(errors);
        var result = GatewayConfigurationFile.Load(path);
        Assert.Equal(ConfigurationLoadKind.Missing, result.Kind);
        Assert.Null(result.Configuration);
        Assert.Empty(result.Errors);
    }

    [Fact]
    public void TryLoad_reads_a_valid_file_and_reports_an_invalid_one()
    {
        string good = TempFile(Example);
        string bad = TempFile("""{ "dis": { "exerciseId": 999 } }""");
        try
        {
            Assert.True(GatewayConfigurationFile.TryLoad(good, out var config, out var errors));
            Assert.Empty(errors);
            Assert.Equal(Load(Example), config);
            Assert.Equal(ConfigurationLoadKind.Loaded, GatewayConfigurationFile.Load(good).Kind);

            Assert.False(GatewayConfigurationFile.TryLoad(bad, out var none, out var problems));
            Assert.Null(none);
            Assert.Single(problems);
            Assert.Contains(bad, problems[0]);
            Assert.Contains("dis.exerciseId", problems[0]);
            Assert.Equal(ConfigurationLoadKind.Invalid, GatewayConfigurationFile.Load(bad).Kind);
        }
        finally
        {
            File.Delete(good);
            File.Delete(bad);
        }
    }

    [Fact]
    public void A_file_with_no_enabled_side_is_loaded_but_runs_nothing()
    {
        string path = TempFile("""{ "dis": { "exerciseId": 1, "enabled": false } }""");
        try
        {
            Assert.True(GatewayConfigurationFile.TryLoad(path, out var config, out _));
            Assert.False(config!.AnySideEnabled);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void The_path_is_the_environment_variable_else_the_file_in_the_current_directory()
    {
        Assert.Equal("SIMNETGATEWAY_CONFIG", GatewayConfigurationFile.PathVariable);
        string custom = Path.Combine(Path.GetTempPath(), "custom.json");
        Assert.Equal(custom, GatewayConfigurationFile.ResolvePath(name => name == "SIMNETGATEWAY_CONFIG" ? custom : null));
        string fallback = Path.Combine(Directory.GetCurrentDirectory(), "simnetgateway.json");
        Assert.Equal(fallback, GatewayConfigurationFile.ResolvePath(_ => null));
        Assert.Equal(fallback, GatewayConfigurationFile.ResolvePath(_ => ""));
        Assert.Equal(fallback, GatewayConfigurationFile.ResolvePath(_ => "  "));
        Assert.Equal(Path.Combine(Directory.GetCurrentDirectory(), GatewayConfigurationFile.DefaultFileName), GatewayConfigurationFile.ResolvePath(_ => null));
        // the real environment lookup agrees with the injected one
        Assert.Equal(GatewayConfigurationFile.ResolvePath(Environment.GetEnvironmentVariable), GatewayConfigurationFile.ResolvePath());
    }
}
