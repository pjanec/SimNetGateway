using SimNetGateway.Protocol.Hrot;
using D = SimNetGateway.Protocol.DdsPlayer;
using Rig = SimNetGateway.Plugin.DdsPlayer.Tests.DpsOutboundTests.Rig;
using Watch = SimNetGateway.Plugin.DdsPlayer.Tests.DpsOutboundTests;

namespace SimNetGateway.Plugin.DdsPlayer.Tests;

/// <summary>T-066 (DD-028): the pivot ambience and weather become the Ambience and Weather state topics - every field mapped, bounded, written once on the world participant whatever the seats, a late client gets the current state (TransientLocal), a withdrawn row is disposed.</summary>
public class DpsAmbienceWeatherTests
{
    private static HrotWeather Weather(int id, HrotVec3[]? outline = null, float feather = 0f, byte precipitation = 0, float mm = 0f) =>
        new(id, outline, feather, 250f, 6f, 9f, precipitation, mm, 8000f, 40f, 150f, 5, 900f, 7.5f, 0.85f);

    [Fact]
    public void Every_ambience_field_reaches_the_topic_and_the_kind_and_intensity_are_bounded()
    {
        uint domain = Watch.NewDomain();
        using var watch = new Watch.Watch<D.Ambience>(domain, "station-1", a => a.Id.ToString());
        using var rig = new Rig(domain);

        rig.Send(new HrotAmbience(7001, "alarm.airraid", 0.75f, true, new HrotVec3(120, 340, 5), 800f, 90.5));
        rig.Send(new HrotAmbience(7002, "battle.distant", 0.6f, false, HrotVec3.Zero, 0f, 0.0));
        rig.Send(new HrotAmbience(7003, new string('k', 50), 7f, false, HrotVec3.Zero, float.NaN, 0.0));

        Assert.True(watch.Until(w => w.Alive.Count == 3));
        var a = watch.Alive["7001"];
        Assert.Equal(("alarm.airraid", 0.75f, true, 120.0, 340.0, 5.0, 800f, 90.5), (a.Kind, a.Intensity, a.HasPosition, a.Position.X, a.Position.Y, a.Position.Z, a.Radius, a.UntilSim));
        Assert.Equal((false, 0f, 0.0), (watch.Alive["7002"].HasPosition, watch.Alive["7002"].Radius, watch.Alive["7002"].UntilSim));
        Assert.Equal((new string('k', 32), 1f, 0f), (watch.Alive["7003"].Kind, watch.Alive["7003"].Intensity, watch.Alive["7003"].Radius));
    }

    [Fact]
    public void Every_weather_field_reaches_the_topic_global_has_no_outline_a_region_has_its_points_and_a_bad_precipitation_is_none()
    {
        uint domain = Watch.NewDomain();
        using var watch = new Watch.Watch<D.Weather>(domain, "station-1", w => w.Id.ToString());
        using var rig = new Rig(domain);

        rig.Send(Weather(8001, precipitation: 3, mm: 1.5f));
        rig.Send(Weather(8002, [new HrotVec3(900, 1900, 10), new HrotVec3(1100, 1900, 10), new HrotVec3(1100, 2100, 10)], feather: 50f, precipitation: 2, mm: 4f));
        rig.Send(Weather(8003, precipitation: 99));
        rig.Send(Weather(8004, Enumerable.Range(0, 40).Select(i => new HrotVec3(i, i, 0)).ToArray()));   // past the bound

        Assert.True(watch.Until(w => w.Alive.Count == 4));
        var g = watch.Alive["8001"];
        Assert.Empty(g.Outline ?? []);
        Assert.Equal((0f, 250f, 6f, 9f, D.PrecipitationKind.PrecipitationKindSnow, 1.5f, 8000f), (g.FeatherM, g.WindFromDeg, g.WindSpeedMps, g.WindGustMps, g.Precipitation, g.PrecipitationMmPerHour, g.VisibilityM));
        Assert.Equal((40f, 150f, (byte)5, 900f, 7.5f, 0.85f), (g.FogTopM, g.FogVisibilityM, g.CloudOctas, g.CloudBaseM, g.GroundTemperatureC, g.RelativeHumidity));
        var r = watch.Alive["8002"];
        Assert.Equal(50f, r.FeatherM);
        Assert.Equal(D.PrecipitationKind.PrecipitationKindRain, r.Precipitation);
        Assert.Equal([(900.0, 1900.0, 10.0), (1100.0, 1900.0, 10.0), (1100.0, 2100.0, 10.0)], r.Outline.Select(p => (p.X, p.Y, p.Z)).ToArray());
        Assert.Equal(D.PrecipitationKind.PrecipitationKindNone, watch.Alive["8003"].Precipitation);
        Assert.Equal(32, watch.Alive["8004"].Outline.Count);
    }

    [Fact]
    public void A_late_client_gets_the_current_state_of_both_topics_and_a_withdrawn_row_is_disposed_once()
    {
        uint domain = Watch.NewDomain();
        using var rig = new Rig(domain);
        var ambienceWrites = new List<(int Id, bool Disposed)>();
        rig.Transport.Ambience.Tap = a => ambienceWrites.Add((a.Id, false));
        rig.Send(new HrotAmbience(7001, "battle.distant", 0.2f, false, HrotVec3.Zero, 0f, 0.0));
        rig.Send(new HrotAmbience(7001, "battle.distant", 0.8f, false, HrotVec3.Zero, 0f, 0.0));   // the state is the last value
        rig.Send(new HrotAmbience(7002, "alarm.airraid", 1f, false, HrotVec3.Zero, 0f, 0.0));
        rig.Send(new HrotAmbienceRemoved(7002));
        rig.Send(new HrotAmbienceRemoved(7002));   // already gone
        rig.Send(new HrotAmbienceRemoved(9999));   // never written
        rig.Send(Weather(8001, mm: 2f));
        rig.Send(Weather(8001, mm: 9f));
        rig.Send(new HrotWeatherRemoved(9999));
        Assert.Equal(3, ambienceWrites.Count);   // a disposal and a removal of an unknown row write no sample

        using var lateAmbience = new Watch.Watch<D.Ambience>(domain, "station-1", a => a.Id.ToString());
        using var lateWeather = new Watch.Watch<D.Weather>(domain, "station-1", w => w.Id.ToString());

        Assert.True(lateAmbience.Until(w => w.Alive.Count == 1));
        Assert.Equal(0.8f, lateAmbience.Alive["7001"].Intensity);
        Assert.True(lateWeather.Until(w => w.Alive.Count == 1));
        Assert.Equal(9f, lateWeather.Alive["8001"].PrecipitationMmPerHour);

        rig.Send(new HrotWeatherRemoved(8001));
        Assert.True(lateWeather.Until(w => w.Alive.Count == 0));
    }

    [Fact]
    public void Ambience_and_weather_are_written_once_on_the_world_participant_whatever_the_seats()
    {
        uint domain = Watch.NewDomain();
        using var oneA = new Watch.Watch<D.Ambience>(domain, "station-1-1", a => a.Id.ToString());
        using var twoA = new Watch.Watch<D.Ambience>(domain, "station-1-2", a => a.Id.ToString());
        using var oneW = new Watch.Watch<D.Weather>(domain, "station-1-1", w => w.Id.ToString());
        using var twoW = new Watch.Watch<D.Weather>(domain, "station-1-2", w => w.Id.ToString());
        using var rig = new Rig(domain, seats: 2);
        int ambience = 0, weather = 0;
        rig.Transport.Ambience.Tap = _ => ambience++;
        rig.Transport.Weather.Tap = _ => weather++;

        rig.Send(new HrotAmbience(7001, "battle.distant", 0.5f, false, HrotVec3.Zero, 0f, 0.0));
        rig.Send(Weather(8001));

        Assert.Equal((1, 1), (ambience, weather));
        foreach (var watch in new[] { oneA, twoA })
        {
            Assert.True(watch.Until(w => w.Events.Count >= 1));
            Thread.Sleep(200);
            watch.Pump();
            Assert.Single(watch.Events);
        }

        foreach (var watch in new[] { oneW, twoW })
        {
            Assert.True(watch.Until(w => w.Events.Count >= 1));
            Thread.Sleep(200);
            watch.Pump();
            Assert.Single(watch.Events);
        }
    }
}
