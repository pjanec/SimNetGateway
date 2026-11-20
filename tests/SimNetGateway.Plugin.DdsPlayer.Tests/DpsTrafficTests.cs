using SimNetGateway.Protocol.Hrot;
using D = SimNetGateway.Protocol.DdsPlayer;
using Rig = SimNetGateway.Plugin.DdsPlayer.Tests.DpsOutboundTests.Rig;
using Watch = SimNetGateway.Plugin.DdsPlayer.Tests.DpsOutboundTests;

namespace SimNetGateway.Plugin.DdsPlayer.Tests;

/// <summary>DPS round 2, ambient traffic (DD-018 3.6b): movers, the batches of knots, and the signal heads.</summary>
public class DpsTrafficTests
{
    private const int Bound = 4096;

    private static string Id(int id) => id.ToString();

    [Fact]
    public void A_mover_is_written_at_spawn_and_disposed_when_it_goes()
    {
        uint domain = Watch.NewDomain();
        using var movers = new Watch.Watch<D.Mover>(domain, "station-1", m => Id(m.MoverId));
        using var rig = new Rig(domain);

        rig.Send(new HrotMover(101, "Car", 8851, 0xB02A2AFF, 0, 4.5f, 1.8f, "Sumo"));
        rig.Send(new HrotMover(205, "Pedestrian", 3002, 0, 205, 0f, 0f, "Sumo"));
        rig.Send(new HrotMover(7, "Hovercraft", 1, 0, 0, 0f, 0f, "Elsewhere"));

        Assert.True(movers.Until(w => w.Alive.Count == 3));
        var car = movers.Alive["101"];
        Assert.Equal((D.MoverKind.MoverKindCar, 8851L, 0xB02A2AFFu, 0u, 4.5f, 1.8f, D.MoverSource.MoverSourceSumo), (car.Kind, car.TypeId, car.ColourRgba, car.Seed, car.Length, car.Width, car.Source));
        var ped = movers.Alive["205"];
        Assert.Equal((D.MoverKind.MoverKindPedestrian, 205u, 0u), (ped.Kind, ped.Seed, ped.ColourRgba));
        Assert.Equal((D.MoverKind.MoverKindUnknown, D.MoverSource.MoverSourceUnknown), (movers.Alive["7"].Kind, movers.Alive["7"].Source));   // a name this contract does not have

        rig.Send(new HrotMoverRemoved(101));
        Assert.True(movers.Until(w => w.Alive.Count == 2 && !w.Alive.ContainsKey("101")));
        rig.Send(new HrotMoverRemoved(999));   // never written: nothing to dispose
        movers.Pump();
        Assert.Equal(2, movers.Alive.Count);
    }

    [Fact]
    public void A_batch_of_knots_is_an_event_with_every_unit_and_is_bounded()
    {
        uint domain = Watch.NewDomain();
        using var batches = new Watch.Watch<D.MoverTracks>(domain, "station-1", b => "batch");
        using var rig = new Rig(domain);
        var knots = Enumerable.Range(0, 5000).Select(i => new HrotMoverKnot(i + 1, -0.05f, 100f + i, 200f, 3.5f, (short)(-9000 + i % 100), 120, 13.9f)).ToArray();

        Assert.True(batches.Until(w =>
        {
            rig.Send(new HrotMoverTracks(42.5, knots));
            rig.Send(new HrotMoverTracks(43.0, []));   // an empty batch writes nothing
            return w.Events.Count > 0;
        }));

        var batch = batches.Events[0];
        Assert.Equal(42.5, batch.SimTime);
        Assert.Equal(Bound, batch.Knots.Count);   // the contract's bound: the rest is left out (the host never sends more)
        Assert.Equal((1, -0.05f, 100f, 200f, 3.5f, (short)-9000, (short)120, 13.9f),
            (batch.Knots[0].MoverId, batch.Knots[0].Dt, batch.Knots[0].X, batch.Knots[0].Y, batch.Knots[0].Z, batch.Knots[0].HeadingCentiDeg, batch.Knots[0].PitchCentiDeg, batch.Knots[0].Speed));
        Assert.DoesNotContain(batches.Events, b => b.SimTime == 43.0);
        Assert.Equal(4096, DpsTrafficOutbound.MaxKnots);
    }

    [Fact]
    public void A_signal_head_is_written_with_its_place_heading_and_lamp_and_disposed_when_it_goes()
    {
        uint domain = Watch.NewDomain();
        using var lights = new Watch.Watch<D.TrafficLights>(domain, "station-1", l => Id(l.SignalId));
        using var rig = new Rig(domain);

        rig.Send(new HrotTrafficLight(4, new HrotVec3(100, -1.7, 5), 90f, "Red"));
        rig.Send(new HrotTrafficLight(5, new HrotVec3(1, 2, 3), 0f, "Flashing"));

        Assert.True(lights.Until(w => w.Alive.Count == 2));
        var red = lights.Alive["4"];
        Assert.Equal((new D.Vec3d { X = 100, Y = -1.7, Z = 5 }, 90f, D.TrafficLightState.TrafficLightStateRed), (red.Position, red.HeadingDeg, red.State));
        Assert.Equal(D.TrafficLightState.TrafficLightStateOff, lights.Alive["5"].State);   // a lamp this contract does not have is off

        rig.Send(new HrotTrafficLight(4, new HrotVec3(100, -1.7, 5), 90f, "Green"));
        Assert.True(lights.Until(w => w.Alive["4"].State == D.TrafficLightState.TrafficLightStateGreen));
        rig.Send(new HrotTrafficLightRemoved(4));
        Assert.True(lights.Until(w => w.Alive.Count == 1 && w.Alive.ContainsKey("5")));
    }

    [Fact]
    public void World_boundary_disposes_movers_and_signal_heads()
    {
        uint domain = Watch.NewDomain();
        using var movers = new Watch.Watch<D.Mover>(domain, "station-1", m => Id(m.MoverId));
        using var lights = new Watch.Watch<D.TrafficLights>(domain, "station-1", l => Id(l.SignalId));
        var rig = new Rig(domain);
        rig.Send(new HrotMover(101, "Car", 8851, 0xFF, 0, 4f, 2f, "Sumo"));
        rig.Send(new HrotTrafficLight(4, default, 0f, "Red"));
        Assert.True(movers.Until(w => w.Alive.Count == 1));
        Assert.True(lights.Until(w => w.Alive.Count == 1));

        rig.Dispose();

        Assert.True(movers.Until(w => w.Alive.Count == 0));
        Assert.True(lights.Until(w => w.Alive.Count == 0));
    }
}
