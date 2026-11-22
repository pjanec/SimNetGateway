using System.Numerics;
using Fdp.Core;
using Fdp.Toolkit.Ambient;
using Fdp.Toolkit.Physics.Components;
using Fdp.Toolkit.Replication.Components;
using Fdp.Toolkit.Time;
using Fdp.Toolkit.Weather;
using Hrot.Map.Common.Components;
using SimNetGateway.Engine;
using SimNetGateway.Engine.InMemory;
using SimNetGateway.Protocol.Hrot;

namespace SimNetGateway.HrotHost.Tests;

/// <summary>
/// T-066 (DD-028): HROT's ambience layers and weather entities. The expected values are what HROT's own components hold (<c>Ambience</c>, <c>WeatherConditions</c>, <c>EditablePolyline</c>); the gathers send each NEW or CHANGED
/// one once (a held one is silent), withdraw a gone one once, state everything again after a world boundary and allocate nothing when steady. The invariant (checked after every gather of a seeded random run): the
/// picture a client builds from the messages equals HROT's state, and a gather of an unchanged world sends nothing.
/// </summary>
public class AmbienceWeatherGatherTests
{
    private sealed class Capture : IIngressSink
    {
        public readonly List<object> All = new();

        public void Deliver<T>(in T message, double? receivedAt = null) => All.Add(message!);

        public List<T> Of<T>() => All.OfType<T>().ToList();
    }

    private sealed class Stage : PackHarness
    {
        private readonly InMemoryInbound _inbound = new(new SourceId(99), "test");

        public Stage()
            : base(dis: true)
        {
            Ensure<Ambience>();
            Ensure<WeatherConditions>();
            Ensure<ScenarioClock>();
            World.RegisterManagedComponent<EditablePolyline>();

            Steps(3);
        }

        public AmbienceGather Ambience { get; } = new();

        public WeatherGather Weather { get; } = new();

        public InMemoryInbound Inbound => _inbound;

        private void Ensure<T>()
            where T : unmanaged
        {
            if (!World.IsComponentTypeRegistered<T>())
            {
                World.RegisterComponent<T>();
            }
        }

        public Entity Layer(int id, string kind, float intensity = 0.6f, float radius = 0f, double until = 0.0, Vector3? at = null)
        {
            var e = World.CreateEntity();
            World.AddComponent(e, new NetworkIdentity(id));
            World.AddComponent(e, new Ambience { Kind = new FixedString32(kind), Intensity = intensity, Radius = radius, Until = new SimInstant(until) });
            if (at is { } p)
            {
                World.AddComponent(e, new SimTransform { Position = p, Rotation = Quaternion.Identity });
            }

            return e;
        }

        public void SetLayer(Entity e, string kind, float intensity, float radius = 0f, double until = 0.0)
            => World.SetComponent(e, new Ambience { Kind = new FixedString32(kind), Intensity = intensity, Radius = radius, Until = new SimInstant(until) });

        public Entity Environment(int id, WeatherConditions w)
        {
            var e = World.CreateEntity();
            World.AddComponent(e, new NetworkIdentity(id));
            World.AddComponent(e, ScenarioClock.Default);
            World.AddComponent(e, w);
            return e;
        }

        public Entity Region(int id, Vector3 origin, Vector2[] points, WeatherConditions w)
        {
            var e = World.CreateEntity();
            World.AddComponent(e, new NetworkIdentity(id));
            World.AddComponent(e, new SimTransform { Position = origin, Rotation = Quaternion.Identity });
            World.SetManagedComponent(e, new EditablePolyline { Points = points });
            World.AddComponent(e, w);
            return e;
        }

        public Capture GatherAmbience()
        {
            Ambience.Gather(World, _inbound);
            return Drain();
        }

        public Capture GatherWeather()
        {
            Weather.Gather(World, _inbound);
            return Drain();
        }

        private Capture Drain()
        {
            var sink = new Capture();
            _inbound.Drain(sink, int.MaxValue);
            return sink;
        }
    }

    private static readonly Vector2[] Square = [new(-100, -100), new(100, -100), new(100, 100), new(-100, 100)];

    private static WeatherConditions Rain()
    {
        var w = WeatherConditions.Standard;
        w.Precipitation = (byte)PrecipitationKind.Rain;
        w.PrecipitationMmPerHour = 4f;
        return w;
    }

    // ---- ambience ------------------------------------------------------------------------------------------------------------------------

    [Fact]
    public void A_layer_is_sent_when_new_again_when_it_changes_never_while_held_and_withdrawn_once_when_gone()
    {
        using var h = new Stage();
        var layer = h.Layer(7001, "battle.distant", 0.6f);

        var first = Assert.Single(h.GatherAmbience().Of<HrotAmbience>());
        Assert.Equal((7001, "battle.distant", 0.6f, false, HrotVec3.Zero, 0f, 0.0), (first.Id, first.Kind, first.Intensity, first.HasPosition, first.Position, first.Radius, first.UntilSim));

        for (int i = 0; i < 30; i++)
        {
            Assert.Empty(h.GatherAmbience().All);   // held: nothing
        }

        h.SetLayer(layer, "battle.distant", 0.9f);
        Assert.Equal(0.9f, Assert.Single(h.GatherAmbience().Of<HrotAmbience>()).Intensity);
        Assert.Empty(h.GatherAmbience().All);

        h.SetLayer(layer, "alarm.airraid", 0.9f, until: 90.0);
        var changed = Assert.Single(h.GatherAmbience().Of<HrotAmbience>());
        Assert.Equal(("alarm.airraid", 90.0), (changed.Kind, changed.UntilSim));

        h.World.DestroyEntity(layer);
        Assert.Equal(7001, Assert.Single(h.GatherAmbience().Of<HrotAmbienceRemoved>()).Id);
        Assert.Empty(h.GatherAmbience().All);   // withdrawn once
    }

    [Fact]
    public void A_positionless_layer_has_no_position_and_no_radius_a_positioned_one_has_both()
    {
        using var h = new Stage();
        h.Layer(7001, "battle.distant", 0.5f, radius: 500f);   // a radius on a positionless layer is ignored by HROT: sent as 0
        h.Layer(7002, "alarm.airraid", 1f, radius: 800f, until: 60.0, at: new Vector3(120, 340, 5));

        var got = h.GatherAmbience().Of<HrotAmbience>().ToDictionary(a => a.Id);

        Assert.Equal((false, HrotVec3.Zero, 0f), (got[7001].HasPosition, got[7001].Position, got[7001].Radius));
        Assert.Equal((true, new HrotVec3(120, 340, 5), 800f, 60.0), (got[7002].HasPosition, got[7002].Position, got[7002].Radius, got[7002].UntilSim));
    }

    [Fact]
    public void A_layer_that_is_being_torn_down_is_withdrawn_at_once()
    {
        using var h = new Stage();
        var layer = h.Layer(7001, "alarm.airraid", 1f);
        Assert.Single(h.GatherAmbience().Of<HrotAmbience>());

        h.World.SetLifecycleState(layer, EntityLifecycle.TearDown);   // what HROT's expiry does at the layer's end

        Assert.Single(h.GatherAmbience().Of<HrotAmbienceRemoved>());
        Assert.Empty(h.GatherAmbience().All);
    }

    [Fact]
    public void A_world_boundary_states_every_layer_again_and_an_out_of_range_intensity_is_clamped()
    {
        using var h = new Stage();
        h.Layer(7001, "battle.distant", 3f);
        h.Layer(7002, "alarm.airraid", -1f);
        var got = h.GatherAmbience().Of<HrotAmbience>().ToDictionary(a => a.Id);
        Assert.Equal((1f, 0f), (got[7001].Intensity, got[7002].Intensity));
        Assert.Empty(h.GatherAmbience().All);

        h.Ambience.Clear();   // a new world

        Assert.Equal(2, h.GatherAmbience().Of<HrotAmbience>().Count);
    }

    // ---- weather -------------------------------------------------------------------------------------------------------------------------

    [Fact]
    public void The_global_weather_has_an_empty_outline_and_every_field_comes_from_hrots_lowest_wind_level_and_lowest_cloud_layer()
    {
        using var h = new Stage();
        var w = WeatherConditions.Standard;
        w.GroundTemperatureC = 7.5f;
        w.RelativeHumidity = 0.85f;
        w.WindCount = 0;
        w.AddWind(3000f, 200f, 20f, 25f);   // out of order on purpose: the lowest is the one at 10 m
        w.AddWind(10f, 250f, 6f, 9f);
        w.CloudCount = 0;
        w.AddCloud(2500f, 500f, 8, CloudKind.Stratus);
        w.AddCloud(900f, 300f, 5, CloudKind.Cumulus);   // the lowest layer
        w.VisibilityM = 8000f;
        w.FogTopM = 40f;
        w.FogVisibilityM = 150f;
        w.Precipitation = (byte)PrecipitationKind.Snow;
        w.PrecipitationMmPerHour = 1.5f;
        w.FeatherM = 77f;   // unused on the global weather: sent as 0
        h.Environment(8001, w);

        var g = Assert.Single(h.GatherWeather().Of<HrotWeather>());

        Assert.Equal(8001, g.Id);
        Assert.True(g.Outline is null or { Length: 0 });
        Assert.Equal((0f, 250f, 6f, 9f), (g.FeatherM, g.WindFromDeg, g.WindSpeedMps, g.WindGustMps));
        Assert.Equal(((byte)3, 1.5f, 8000f, 40f, 150f), (g.Precipitation, g.PrecipitationMmPerHour, g.VisibilityM, g.FogTopM, g.FogVisibilityM));
        Assert.Equal(((byte)5, 900f, 7.5f, 0.85f), (g.CloudOctas, g.CloudBaseM, g.GroundTemperatureC, g.RelativeHumidity));
    }

    [Fact]
    public void Defaults_for_what_hrot_has_no_value_for_calm_wind_clear_sky_and_no_fog_visibility_without_a_fog_layer()
    {
        using var h = new Stage();
        var w = WeatherConditions.Standard;   // FogVisibilityM 200 but FogTopM 0: no fog
        w.CloudCount = 0;
        w.WindCount = 0;
        h.Environment(8001, w);

        var g = Assert.Single(h.GatherWeather().Of<HrotWeather>());

        Assert.Equal((0f, 0f, 0f), (g.WindFromDeg, g.WindSpeedMps, g.WindGustMps));
        Assert.Equal(((byte)0, 0f), (g.CloudOctas, g.CloudBaseM));
        Assert.Equal((0f, 0f), (g.FogTopM, g.FogVisibilityM));
    }

    [Fact]
    public void A_region_carries_its_outline_in_the_simulation_frame_and_its_feather_the_global_row_stays()
    {
        using var h = new Stage();
        h.Environment(8001, Rain());
        var rw = Rain();
        rw.FeatherM = 50f;
        h.Region(8002, new Vector3(1000, 2000, 10), Square, rw);

        var got = h.GatherWeather().Of<HrotWeather>().ToDictionary(x => x.Id);

        Assert.Equal(2, got.Count);
        Assert.True(got[8001].Outline is null or { Length: 0 });
        Assert.Equal(50f, got[8002].FeatherM);
        Assert.Equal([new HrotVec3(900, 1900, 10), new HrotVec3(1100, 1900, 10), new HrotVec3(1100, 2100, 10), new HrotVec3(900, 2100, 10)], got[8002].Outline);
    }

    [Fact]
    public void Weather_is_sent_on_change_only_an_edit_a_move_or_a_new_outline_counts_a_held_one_is_silent()
    {
        using var h = new Stage();
        var global = h.Environment(8001, Rain());
        var region = h.Region(8002, new Vector3(0, 0, 0), Square, Rain());
        Assert.Equal(2, h.GatherWeather().Of<HrotWeather>().Count);
        for (int i = 0; i < 30; i++)
        {
            Assert.Empty(h.GatherWeather().All);
        }

        var heavier = Rain();
        heavier.PrecipitationMmPerHour = 12f;
        h.World.SetComponent(global, heavier);
        Assert.Equal(12f, Assert.Single(h.GatherWeather().Of<HrotWeather>()).PrecipitationMmPerHour);
        Assert.Empty(h.GatherWeather().All);

        h.World.SetComponent(region, new SimTransform { Position = new Vector3(50, 0, 0), Rotation = Quaternion.Identity });   // moved: the outline is relative, the footprint moved
        var moved = Assert.Single(h.GatherWeather().Of<HrotWeather>());
        Assert.Equal((8002, 50.0 - 100.0), (moved.Id, moved.Outline![0].X));

        h.World.SetManagedComponent(region, new EditablePolyline { Points = [new(-10, -10), new(10, -10), new(0, 10)] });
        Assert.Equal(3, Assert.Single(h.GatherWeather().Of<HrotWeather>()).Outline!.Length);
        Assert.Empty(h.GatherWeather().All);

        h.World.DestroyEntity(region);
        Assert.Equal(8002, Assert.Single(h.GatherWeather().Of<HrotWeatherRemoved>()).Id);
        Assert.Empty(h.GatherWeather().All);
    }

    [Fact]
    public void No_world_environment_means_no_global_row_and_an_area_without_weather_or_weather_without_an_area_is_no_region()
    {
        using var h = new Stage();
        h.Region(8002, Vector3.Zero, Square, Rain());
        var shapeless = h.World.CreateEntity();   // weather with no area
        h.World.AddComponent(shapeless, new NetworkIdentity(8003));
        h.World.AddComponent(shapeless, new SimTransform { Position = Vector3.Zero, Rotation = Quaternion.Identity });
        h.World.AddComponent(shapeless, Rain());
        var tooSmall = h.Region(8004, Vector3.Zero, [new(0, 0), new(1, 1)], Rain());   // fewer than three points is no area (WeatherField.TryOutline)

        var got = h.GatherWeather().Of<HrotWeather>();

        Assert.Equal([8002], got.Select(x => x.Id).ToArray());
    }

    [Fact]
    public void A_long_outline_is_sampled_down_to_the_topic_bound_keeping_the_first_point()
    {
        using var h = new Stage();
        var ring = Enumerable.Range(0, 100).Select(i => new Vector2(100f * MathF.Cos(i * MathF.Tau / 100), 100f * MathF.Sin(i * MathF.Tau / 100))).ToArray();
        h.Region(8002, Vector3.Zero, ring, Rain());

        var r = Assert.Single(h.GatherWeather().Of<HrotWeather>());

        Assert.Equal(WeatherGather.MaxOutline, r.Outline!.Length);
        Assert.Equal((100.0, 0.0), (r.Outline[0].X, r.Outline[0].Y));
        Assert.Equal(ring[3].X, (float)r.Outline[1].X);   // evenly: every 100/32 points (3.125 -> 3)
    }

    [Fact]
    public void A_world_boundary_states_every_weather_again()
    {
        using var h = new Stage();
        h.Environment(8001, Rain());
        h.Region(8002, Vector3.Zero, Square, Rain());
        Assert.Equal(2, h.GatherWeather().Of<HrotWeather>().Count);
        h.Weather.Clear();
        Assert.Equal(2, h.GatherWeather().Of<HrotWeather>().Count);
    }

    // ---- invariants ----------------------------------------------------------------------------------------------------------------------

    /// <summary>A seeded random run of create / change / hold / tear down / destroy: after EVERY gather the client's picture equals HROT's layers, and a gather of an unchanged world sends nothing.</summary>
    [Theory]
    [InlineData(1)]
    [InlineData(7)]
    [InlineData(2026)]
    public void Seeded_runs_keep_the_clients_ambience_picture_equal_to_hrots_and_send_nothing_when_nothing_changed(int seed)
    {
        using var h = new Stage();
        var random = new Random(seed);
        var layers = new Dictionary<int, Entity>();
        var picture = new Dictionary<int, HrotAmbience>();
        string[] kinds = ["battle.distant", "alarm.airraid", "alarm", "traffic.city"];
        int next = 7000;
        for (int step = 0; step < 300; step++)
        {
            bool changed = false;
            switch (random.Next(6))
            {
                case 0:
                    int id = ++next;
                    layers[id] = h.Layer(id, kinds[random.Next(kinds.Length)], random.Next(11) / 10f, 300f, 0.0, random.Next(2) == 0 ? new Vector3(random.Next(500), random.Next(500), 0) : null);
                    changed = true;
                    break;
                case 1 when layers.Count > 0:
                    var pick = layers.ElementAt(random.Next(layers.Count));
                    h.SetLayer(pick.Value, kinds[random.Next(kinds.Length)], random.Next(11) / 10f, 300f);
                    changed = true;
                    break;
                case 2 when layers.Count > 0:
                    var gone = layers.ElementAt(random.Next(layers.Count));
                    if (random.Next(2) == 0)
                    {
                        h.World.DestroyEntity(gone.Value);
                    }
                    else
                    {
                        h.World.SetLifecycleState(gone.Value, EntityLifecycle.TearDown);
                    }

                    layers.Remove(gone.Key);
                    changed = true;
                    break;
            }

            var sent = h.GatherAmbience();
            foreach (var m in sent.All)
            {
                if (m is HrotAmbience a)
                {
                    picture[a.Id] = a;
                }
                else if (m is HrotAmbienceRemoved r)
                {
                    Assert.True(picture.Remove(r.Id));
                }
            }

            if (!changed)
            {
                Assert.Empty(sent.All);
            }

            Assert.Equal(layers.Keys.Order().ToArray(), picture.Keys.Order().ToArray());
            foreach (var (lid, entity) in layers)
            {
                var component = h.World.GetComponentRO<Ambience>(entity);
                Assert.Equal((component.Kind.ToString(), component.Intensity), (picture[lid].Kind, picture[lid].Intensity));
            }
        }
    }

    /// <summary>0 B per steady gather: layers, the global weather and a region all held.</summary>
    [Fact]
    public void A_steady_gather_allocates_nothing()
    {
        using var h = new Stage();
        h.Layer(7001, "battle.distant", 0.6f);
        h.Layer(7002, "alarm.airraid", 1f, 800f, 60.0, new Vector3(1, 2, 3));
        h.Environment(8001, Rain());
        h.Region(8002, new Vector3(10, 20, 0), Square, Rain());
        for (int i = 0; i < 5; i++)
        {
            h.GatherAmbience();
            h.GatherWeather();
        }

        var inbound = new InMemoryInbound(new SourceId(98), "alloc");
        var ambience = h.Ambience;
        var weather = h.Weather;
        for (int i = 0; i < 50; i++)
        {
            ambience.Gather(h.World, inbound);
            weather.Gather(h.World, inbound);
        }

        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 300; i++)
        {
            ambience.Gather(h.World, inbound);
            weather.Gather(h.World, inbound);
        }

        Assert.Equal(0L, GC.GetAllocatedBytesForCurrentThread() - before);
    }
}
