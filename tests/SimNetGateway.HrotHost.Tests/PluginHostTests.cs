using System.Numerics;
using SimNetGateway.Engine;
using SimNetGateway.Gateway.HrotDis;
using SimNetGateway.Plugin.Hla;
using SimNetGateway.Plugin.Hla.Rti.Fake;
using GatewayConfiguration = SimNetGateway.Gateway.HrotDis.GatewayConfiguration;

namespace SimNetGateway.HrotHost.Tests;

/// <summary>A side with nothing to do, to see what the host does for a plugin: its service, its lease, its pump and the replay flag.</summary>
public sealed class ProbeSide : IProtocolSide
{
    public const string SideKey = "probe";

    public string Key => SideKey;

    public object? Parse(ConfigSection section)
    {
        bool replay = section.Boolean("replay", false);
        section.Finish();
        return new ProbeSettings(replay);
    }

    public void Write(System.Text.Json.Utf8JsonWriter writer, object settings) => writer.WriteBoolean("replay", ((ProbeSettings)settings).Replay);

    public bool Runs(object settings) => true;

    public void Declare(SideDeclaration declaration, object settings)
    {
    }

    public void Register(SideContext context, object settings)
    {
    }
}

public sealed record ProbeSettings(bool Replay);

public sealed class ProbePlugin : IGatewayPlugin
{
    public List<string> Events { get; } = new();

    /// <summary>Runs in the plugin's pump with its binding (a test plays a plugin that calls the player station from its pump).</summary>
    public Action<SideBinding>? OnPump { get; set; }

    public string Name => "probe";

    public int InterfaceVersion => PluginInterface.Version;

    public IProtocolSide Side { get; } = new ProbeSide();

    public IHostService? CreateHostService(object settings, PluginHostContext context) => new Service(Events);

    public PluginBound Bind(SideBinding binding)
    {
        Events.Add("bind");
        Assert.NotNull(binding.Service);
        return new PluginBound(new Lease(Events), () =>
        {
            Events.Add("pump");
            OnPump?.Invoke(binding);
        });
    }

    public bool WantsEventsInReplay(object settings) => ((ProbeSettings)settings).Replay;

    private sealed class Service(List<string> events) : IHostService
    {
        public void Pump() => events.Add("service-pump");

        public void Dispose() => events.Add("service-dispose");
    }

    private sealed class Lease(List<string> events) : IDisposable
    {
        public void Dispose() => events.Add("lease-dispose");
    }
}

/// <summary>What the host does for the plugins (DD-016): host services, leases, pumps, replay gathering, the HLA plugin on the fake RTI.</summary>
public sealed class PluginHostTests
{
    private static PackHarness WithProbe(ProbePlugin probe, bool replay = false) =>
        new(plugins: [probe], configure: c => c.WithSide(ProbeSide.SideKey, new ProbeSettings(replay)));

    [Fact]
    public void The_default_plugin_folder_is_beside_the_host_assembly()
    {
        Assert.Equal(Path.Combine(Path.GetDirectoryName(typeof(GatewayExtension).Assembly.Location)!, "plugins"), PluginSet.DefaultFolder);
    }

    [Fact]
    public void A_service_is_pumped_every_frame_and_disposed_after_the_runtime_and_its_lease_end()
    {
        var probe = new ProbePlugin();
        using var h = WithProbe(probe);
        h.AddHrotEntity(2001, new Vector3(10, 20, 0), "ALPHA");
        h.Steps(3);
        Assert.Equal(1, probe.Events.Count(e => e == "bind"));
        Assert.True(probe.Events.Count(e => e == "service-pump") >= 3);
        Assert.True(probe.Events.Count(e => e == "pump") >= 1);
        Assert.DoesNotContain("lease-dispose", probe.Events);

        h.System.Dispose();
        Assert.Equal("lease-dispose", probe.Events[^1]);
        h.HostServices.Dispose();
        Assert.Equal(["lease-dispose", "service-dispose"], probe.Events.TakeLast(2));
    }

    [Fact]
    public void A_world_boundary_ends_the_lease_and_not_the_service()
    {
        var probe = new ProbePlugin();
        using var h = WithProbe(probe);
        h.Steps(2);
        h.World.Dispose();
        probe.Events.Clear();
        h.System.Dispose();
        Assert.Contains("lease-dispose", probe.Events);
        Assert.DoesNotContain("service-dispose", probe.Events);
    }

    [Fact]
    public void A_plugin_that_wants_events_in_replay_turns_the_replay_gathering_on()
    {
        var quiet = new ProbePlugin();
        using (var h = WithProbe(quiet, replay: false))
        {
            h.Steps(2);
            Assert.False(h.System.Runtime!.EventsInReplay);
        }

        var wants = new ProbePlugin();
        using (var h = WithProbe(wants, replay: true))
        {
            h.Steps(2);
            Assert.True(h.System.Runtime!.EventsInReplay);
        }
    }

    [Fact]
    public void The_hla_plugin_joins_the_federation_of_the_fake_rti_and_leaves_it_with_the_runtime()
    {
        var rti = new FakeRtiBinding();
        using var h = new PackHarness(dis: false, plugins: [new HlaPlugin([rti])],
            configure: c => c.WithSide("hla", new HlaConfiguration { Federation = "RPR-Test" }));
        h.AddHrotEntity(2001, new Vector3(10, 20, 0), "ALPHA");
        h.Steps(5);
        Assert.Empty(h.Errors);
        Assert.Equal(1, rti.Hub.JoinedFederateCount("RPR-Test"));

        h.System.Dispose();
        Assert.Equal(0, rti.Hub.JoinedFederateCount("RPR-Test"));
    }
}
