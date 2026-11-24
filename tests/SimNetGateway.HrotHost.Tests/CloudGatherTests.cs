using System.Numerics;
using Fdp.Core;
using Fdp.Toolkit.Combat;
using Fdp.Toolkit.Physics.Components;
using Fdp.Toolkit.Replication.Components;
using Fdp.Toolkit.Time;
using Fdp.Toolkit.Weather;
using CloudKind = Fdp.Toolkit.Combat.CloudKind;
using SimNetGateway.Engine;
using SimNetGateway.Engine.InMemory;
using SimNetGateway.Protocol.Hrot;

namespace SimNetGateway.HrotHost.Tests;

/// <summary>
/// DD-033 (T-078): gas and smoke clouds. The properties: <c>HrotCloud</c> equals HROT's <c>AreaEffectCloud</c> + <c>SimTransform</c> on the same world, its drift is the wind HROT drifts it on
/// (<c>AreaEffectClouds.DriftWind</c>), a wind change is one new sample, the end is one dispose, a cloud is never also an entity, and a steady cloud sends nothing and allocates nothing.
/// </summary>
public class CloudGatherTests
{
    private sealed class Capture : IIngressSink
    {
        public readonly List<object> All = new();

        public void Deliver<T>(in T message, double? receivedAt = null) => All.Add(message!);

        public List<T> Of<T>() => All.OfType<T>().ToList();
    }

    private sealed class Stage : PackHarness
    {
        private double _now = 1000;
        private Entity _environment;

        public Stage()
            : base(dis: true)
        {
            Ensure<AreaEffectCloud>();
            Ensure<WeatherConditions>();
            Ensure<ScenarioClock>();
            Steps(3);
        }

        private void Ensure<T>()
            where T : unmanaged
        {
            if (!World.IsComponentTypeRegistered<T>())
            {
                World.RegisterComponent<T>();
            }
        }

        public Capture Gather(double simTime)
        {
            var inbound = new InMemoryInbound(new SourceId(99), "test");
            _now += 0.1;
            Adapter.GatherEntities(World, inbound, _now, simTime);
            var sink = new Capture();
            inbound.Drain(sink, int.MaxValue);
            return sink;
        }

        /// <summary>A steady wind blowing TOWARD <paramref name="toward"/> (m/s), at every height; zero removes the weather's effect.</summary>
        public void Wind(Vector2 toward)
        {
            if (_environment.IsNull)
            {
                _environment = World.CreateEntity();
                World.AddComponent(_environment, new NetworkIdentity(9100));
                World.AddComponent(_environment, ScenarioClock.Default);
                World.AddComponent(_environment, WeatherConditions.SteadyWind(toward));
            }
            else
            {
                World.SetComponent(_environment, WeatherConditions.SteadyWind(toward));
            }
        }

        /// <summary>A cloud as HROT's request makes it: a replicated entity (network id, the cloud's TKB type) with a transform and an <c>AreaEffectCloud</c>.</summary>
        public Entity Cloud(int id, CloudKind kind, Vector3 at, float radius = 8f, float height = 6f, float transmittance = 0.1f, double born = 100.0, float seconds = 60f)
        {
            var e = World.CreateEntity();
            World.AddComponent(e, new NetworkIdentity(id));
            World.AddComponent(e, new TkbIdentity { TkbType = new TkbTypeId(AreaEffectClouds.TkbType) });
            World.AddComponent(e, new SimTransform { Position = at, Rotation = Quaternion.Identity });
            World.AddComponent(e, new AreaEffectCloud
            {
                Kind = kind, Radius = radius, Height = height, SightTransmittance = transmittance, BornAtSimSeconds = new SimInstant(born), Seconds = new SimDuration(seconds),
            });
            return e;
        }
    }

    private static HrotCloudKind Named(CloudKind kind) => Enum.Parse<HrotCloudKind>(kind.ToString());

    [Theory]
    [InlineData(CloudKind.Smoke)]
    [InlineData(CloudKind.TearGas)]
    public void A_cloud_equals_HROTs_components_and_is_never_an_entity(CloudKind kind)
    {
        using var h = new Stage();
        var e = h.Cloud(5001, kind, new Vector3(10, 20, 3), radius: 8f, height: 6f, transmittance: 0.25f, born: 100.0, seconds: 90f);

        var all = h.Gather(150.0);

        var c = Assert.Single(all.Of<HrotCloud>());
        var cloud = h.World.GetComponentRO<AreaEffectCloud>(e);
        Assert.Equal((5001, Named(kind), 150.0, new HrotVec3(10, 20, 3), 8f, 6f, 0.25f, 100.0, 90f),
            (c.Id, c.Kind, c.SimTime, c.Centre, cloud.Radius, cloud.Height, c.SightTransmittance, c.BornAt, c.Seconds));
        Assert.Equal((cloud.Radius, cloud.Height), (c.Radius, c.Height));
        Assert.Equal(HrotVec3.Zero, c.Drift);   // no weather: where it burst
        Assert.DoesNotContain(all.All.OfType<HrotEntityState>(), s => s.Id == 5001);   // no Entity, no EntityState: the Cloud topic is all of it
        Assert.Empty(h.Gather(151.0).Of<HrotCloud>());   // steady: nothing
    }

    [Fact]
    public void The_drift_is_the_wind_HROT_drifts_it_on_and_a_wind_change_is_exactly_one_new_sample()
    {
        using var h = new Stage();
        h.Wind(new Vector2(2f, -1f));
        var e = h.Cloud(5001, CloudKind.Smoke, new Vector3(10, 20, 3));
        var at = h.World.GetComponentRO<SimTransform>(e).Position;
        var expected = AreaEffectClouds.DriftWind(h.World, at, 6f);
        Assert.NotEqual(Vector2.Zero, expected);   // the fixture has a wind

        var first = Assert.Single(h.Gather(150.0).Of<HrotCloud>());
        Assert.Equal(new HrotVec3(expected.X, expected.Y, 0), first.Drift);
        Assert.Empty(h.Gather(150.5).Of<HrotCloud>());   // the same wind: nothing

        h.Wind(new Vector2(-3f, 0f));
        var turned = Assert.Single(h.Gather(151.0).Of<HrotCloud>());
        var now = AreaEffectClouds.DriftWind(h.World, at, 6f);
        Assert.Equal(new HrotVec3(now.X, now.Y, 0), turned.Drift);
        Assert.NotEqual(first.Drift, turned.Drift);
        Assert.Empty(h.Gather(151.5).Of<HrotCloud>());
    }

    [Fact]
    public void A_cloud_that_sits_on_its_source_does_not_drift_and_a_size_or_kind_change_is_one_sample()
    {
        using var h = new Stage();
        h.Wind(new Vector2(2f, 0f));
        var source = h.World.CreateEntity();
        h.World.AddComponent(source, new NetworkIdentity(5100));
        var e = h.Cloud(5001, CloudKind.TearGas, new Vector3(10, 20, 3));
        var tf = h.World.GetComponentRO<SimTransform>(e);
        Assert.NotEqual(Vector2.Zero, AreaEffectClouds.DriftWind(h.World, tf.Position, 6f));
        Assert.NotEqual(HrotVec3.Zero, Assert.Single(h.Gather(150.0).Of<HrotCloud>()).Drift);

        var cloud = h.World.GetComponentRO<AreaEffectCloud>(e);
        cloud.SourceNetworkId = 5100;   // a venting canister that lives: HROT does not drift it, so neither do we
        h.World.SetComponent(e, cloud);
        h.Map.Register(5100, source);
        Assert.Equal(HrotVec3.Zero, Assert.Single(h.Gather(151.0).Of<HrotCloud>()).Drift);

        cloud.Radius = 12f;   // a size change alone
        h.World.SetComponent(e, cloud);
        Assert.Equal(12f, Assert.Single(h.Gather(152.0).Of<HrotCloud>()).Radius);

        cloud.Kind = CloudKind.Smoke;
        h.World.SetComponent(e, cloud);
        Assert.Equal(HrotCloudKind.Smoke, Assert.Single(h.Gather(153.0).Of<HrotCloud>()).Kind);
    }

    [Fact]
    public void A_cloud_that_ends_is_sent_ended_exactly_once()
    {
        using var h = new Stage();
        var e = h.Cloud(5001, CloudKind.Smoke, new Vector3(1, 2, 3));
        Assert.Single(h.Gather(150.0).Of<HrotCloud>());

        h.World.DestroyEntity(e);

        var gone = h.Gather(151.0);
        Assert.Equal(5001, Assert.Single(gone.Of<HrotCloudEnded>()).Id);
        Assert.DoesNotContain(gone.All, m => m is HrotEntityRemoved);   // it was never an entity
        Assert.Empty(h.Gather(152.0).Of<HrotCloudEnded>());
    }

    [Fact]
    public void A_steady_cloud_gather_allocates_nothing_and_sends_nothing()
    {
        using var h = new Stage();
        h.Wind(new Vector2(2f, -1f));
        var e = h.Cloud(5001, CloudKind.Smoke, new Vector3(10, 20, 3));
        var gather = new CloudGather();
        var inbound = new InMemoryInbound(new SourceId(99), "steady");
        void One(double t)
        {
            gather.Begin();
            gather.Gather(h.World, e, 5001, t, inbound);
            gather.End(inbound);
        }

        One(150.0);
        Assert.Equal(1, inbound.Drain(new Capture(), int.MaxValue));
        for (int i = 0; i < 50; i++)
        {
            One(150.0);
        }

        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 300; i++)
        {
            One(150.0);
        }

        long bytes = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.Equal(0, inbound.Drain(new Capture(), int.MaxValue));
        Assert.Equal(0L, bytes);
    }
}
