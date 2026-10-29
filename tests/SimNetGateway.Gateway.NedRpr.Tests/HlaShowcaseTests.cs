using SimNetGateway.Engine;
using SimNetGateway.Protocol.Ned;
using SimNetGateway.Protocol.Rpr;
using SimNetGateway.Protocol.Rpr.Fom;
using SimNetGateway.Showcase;

namespace SimNetGateway.Gateway.NedRpr.Tests;

/// <summary>
/// Stage HLA-3 (DD-008 sections 6 and 12b): the RPR side of the host assembly is the HLA transport on the fake RTI plus the
/// scripted RPR federate. Wiring, the order of a host tick, what the engine receives from the RTI, and the invariant G-9
/// (what the RPR side receives is what the gateway sent) including its detection of a message that never arrived.
/// </summary>
public class HlaShowcaseTests
{
    // ---- the wiring and the tick order -----------------------------------------------------------------------------------

    [Fact]
    public void The_host_wires_the_rpr_port_to_the_hla_transport_and_a_scripted_federate_on_one_fake_rti()
    {
        var h = new GatewayHarness();

        Assert.True(h.Host.RprConnection.IsStarted);
        Assert.Equal(2, h.Host.Hub.JoinedFederateCount("RPR-Showcase")); // the gateway and the scripted federate
        Assert.Equal("RprPeer", h.Host.RprPeer.Name);
        Assert.Equal(h.RprSource, h.Host.RprConnection.Source); // the engine binds the transport as the "rpr" inbound channel
    }

    [Fact]
    public void One_host_tick_is_pump_then_engine_tick_then_the_federates_evoke()
    {
        var h = new GatewayHarness();

        // 1. the pump comes BEFORE the engine tick: what the federate said before the step is in the repository after it
        h.Host.SendRpr(S.Platform(1));
        h.Step();
        Assert.NotNull(h.EntityOfHandle(h.Host.ActualHandle(0x601)));

        // 2. the federate's evoke comes AFTER the engine tick: what the gateway sent into the RTI during the step is decoded by its end
        h.Ned(T.Master(T.Hawk, "Hawk"));
        h.Ned(T.Geo(T.Hawk));
        h.Step();
        Assert.Single(h.RprSent<RprRegisterObject>());
        Assert.Single(h.Host.RprPeer.Discovered);
        long registrationTick = h.Engine.TickCount;

        // 3. the registration is answered by the NEXT pump, so the first update goes out one tick after the request
        Assert.Empty(h.RegisteredHandles);
        h.Step();
        var handle = Assert.Single(h.RegisteredHandles);
        var update = Assert.Single(h.RprSent<RprObjectUpdate>());
        Assert.Equal(handle, update.Handle);
        Assert.Equal(registrationTick + 1, h.RprOut.Messages.Last(r => r.Message is RprObjectUpdate).Tick);
        var seen = h.Host.RprPeer.DiscoveredObject(handle.Value);
        Assert.NotNull(seen);
        Assert.Equal(h.Engine.TickCount, seen!.LastReflectTick); // received in the tick it was sent
        Assert.Equal("Hawk", seen.Marking!.Value.AsciiText);
    }

    [Fact]
    public void The_handle_of_a_registration_is_the_one_the_rti_gave_the_object_and_the_federate_discovered()
    {
        var h = new GatewayHarness();
        var hawk = T.PublishNed(h, T.Hawk, "Hawk");
        var viper = T.PublishNed(h, T.Viper, "Viper", 100, T.Geo(T.Viper, lat: 50.1));

        var handles = new[] { h.HandleOf(hawk), h.HandleOf(viper) };

        Assert.NotEqual(handles[0], handles[1]);
        Assert.Equal(handles, h.RegisteredHandles);
        foreach (var handle in handles)
        {
            Assert.True(h.Host.RprConnection.TryGetObjectName(handle, out string name));
            Assert.Equal(name, h.Host.RprPeer.NameOfHandle(handle.Value)); // the federate knows the same object under the same name
            Assert.Equal(RprFom2.ObjectClasses.PhysicalEntity, h.Host.RprPeer.DiscoveredObject(handle.Value)!.ClassName);
        }
    }

    [Fact]
    public void Without_a_pump_nothing_the_federate_said_reaches_the_gateway_and_no_registration_is_answered()
    {
        var h = new GatewayHarness { DeliverRti = false };
        h.Host.SendRpr(S.Platform(1));

        h.Steps(3);
        Assert.Equal(0, h.Engine.State.Count);

        h.DeliverRti = true;
        h.Step();
        Assert.Equal(1, h.Engine.State.Count);
    }

    [Fact]
    public void The_direct_queue_reaches_the_engine_without_crossing_the_rti()
    {
        var h = new GatewayHarness();

        h.RprStep(T.RprFull(0x9001, "Direct", 400, 50, 14, 300, 90, 0, 3));

        Assert.NotNull(h.EntityOfHandle(new RprObjectHandle(0x9001)));
        Assert.Equal(0, h.Host.RprPeer.InjectedObjectCount);
        Assert.Equal(0, h.Host.RprConnection.EventCorrelationCount);
    }

    // ---- what the engine receives from the RTI ----------------------------------------------------------------------------

    [Fact]
    public void Events_arrive_with_the_guid_the_transport_correlated_and_the_host_maps_the_scripts_id_to_it()
    {
        var h = new GatewayHarness();
        var delivered = new List<IRprMessage>();
        h.Host.OnRprDelivered = delivered.Add;
        var scripted = Guid.Parse("e1000000-0000-4000-8000-000000000001");
        h.Host.SendRpr(S.Platform(1));
        h.Step();

        h.Host.SendRpr(new RprWeaponFire(scripted, h.Host.ActualHandle(0x601), null, new WorldLocation(3_973_000, 999_000, 4_871_000), T.Dis(501), 1));
        h.Step();

        var fire = Assert.Single(delivered.OfType<RprWeaponFire>());
        Assert.NotEqual(scripted, fire.EventId); // HLA carries no guid: the transport made one
        Assert.Equal(fire.EventId, h.Host.SeenEventId(scripted));
        var other = Guid.Parse("e2000000-0000-4000-8000-000000000002");
        Assert.Equal(other, h.Host.SeenEventId(other)); // an id of an event that never arrived maps to itself
        Assert.Equal(h.Host.ActualHandle(0x601), fire.FiringObject);
    }

    [Fact]
    public void The_transport_hands_registration_answers_to_the_host_in_order_and_remembers_them()
    {
        var h = new GatewayHarness();
        var delivered = new List<RprObjectRegistered>();
        h.Host.OnRprDelivered = m =>
        {
            if (m is RprObjectRegistered r)
            {
                delivered.Add(r);
            }
        };

        T.PublishNed(h, T.Hawk, "Hawk");
        T.PublishNed(h, T.Viper, "Viper", 100, T.Geo(T.Viper, lat: 50.1));

        Assert.Equal(2, delivered.Count);
        Assert.Equal(delivered.Select(d => d.Handle), h.RegisteredHandles);
        Assert.All(h.RegisteredHandles, handle => Assert.True(h.Host.IsRegisteredHandle(handle)));
        Assert.False(h.Host.IsRegisteredHandle(new RprObjectHandle(0x7777)));
        Assert.Equal(2, h.Host.RegisteredCount);
    }

    // ---- G-9: what the RPR side receives is what the gateway sent -----------------------------------------------------------

    private static GatewayHarness WithRtiChecks()
    {
        var h = new GatewayHarness();
        h.Checker.CheckRti = true;
        return h;
    }

    [Fact]
    public void G9_holds_while_the_gateway_publishes_updates_and_removes_an_entity()
    {
        var h = WithRtiChecks();
        var hawk = T.PublishNed(h, T.Hawk, "Hawk");
        h.NedStep(T.Geo(T.Hawk, lat: 50.01)); // a partial update of the spatial group
        h.NedStep(new NedEntityDamage(T.Hawk, 2)); // a damage state
        h.Run(6.0); // a heartbeat
        h.NedStep(new NedEntityDisposed(T.Hawk)); // a removal

        Assert.Equal(0, h.Engine.State.Count);
        Assert.NotNull(hawk);
        Assert.NotEmpty(h.RprSent<RprObjectRemoved>());
    }

    [Fact]
    public void G9_a_static_entity_that_moves_stays_a_static_variant_at_the_federate_because_the_transport_merges_the_spatial_state()
    {
        var h = WithRtiChecks();
        var gate = T.PublishNed(h, T.Gate, "Gate", 300, T.Geo(T.Gate, speed: 0)); // the full update says: dead reckoning Static
        var handle = h.HandleOf(gate);

        h.NedStep(T.Geo(T.Gate, alt: 320.0, speed: 0)); // it is lifted 20 m: a partial update of the location alone (no orientation, no algorithm)
        h.Steps(2);

        var partial = h.RprSent<RprObjectUpdate>().Last();
        Assert.NotNull(partial.WorldLocation);
        Assert.Null(partial.Orientation);
        Assert.Null(partial.DeadReckoningAlgorithm);
        var seen = h.Host.RprPeer.DiscoveredObject(handle.Value)!;
        var variant = seen.Spatial!.Value;
        Assert.Equal(DeadReckoningAlgorithmEnum8.Static, variant.Algorithm);
        Assert.Equal(partial.WorldLocation!.Value.Z, variant.WorldLocation.Z);
        Assert.NotEqual(default, variant.Orientation); // the orientation of the first update is still part of the one variant record
        Assert.Equal(2, seen.Count("Spatial"));
        Assert.Empty(h.Checker.MirrorDifferences(handle));
    }

    [Fact]
    public void G9_detects_an_update_the_federate_does_not_hold()
    {
        var h = WithRtiChecks();
        var hawk = T.PublishNed(h, T.Hawk, "Hawk");
        var handle = h.HandleOf(hawk);

        // the checker is told about a marking that never went through the RTI
        h.Checker.OnRprSent(new RprObjectUpdate(handle) { Marking = "FORGED" });
        var ex = Assert.Throws<InvariantViolationException>(() => h.Step());

        Assert.Contains("G-9", ex.Message);
        Assert.Contains("marking 'FORGED' arrived as 'Hawk'", ex.Message);
    }

    [Fact]
    public void G9_detects_a_handle_the_federate_never_discovered()
    {
        var h = WithRtiChecks();

        h.Checker.OnRprSent(new RprObjectUpdate(new RprObjectHandle(0x5555)) { Marking = "GHOST" });
        var ex = Assert.Throws<InvariantViolationException>(() => h.Step());

        Assert.Contains("never discovered", ex.Message);
    }

    [Fact]
    public void G9_detects_a_registration_that_did_not_reach_the_federate()
    {
        var h = WithRtiChecks();

        h.Checker.OnRprSent(new RprRegisterObject(1, T.Dis(200)));
        var ex = Assert.Throws<InvariantViolationException>(() => h.Step());

        Assert.Contains("registered 1 objects, the RPR federate discovered 0", ex.Message);
    }

    [Fact]
    public void G9_detects_a_fire_and_a_detonation_that_did_not_arrive()
    {
        var h = WithRtiChecks();
        h.Checker.OnRprSent(new RprWeaponFire(Guid.NewGuid(), null, null, default, T.Dis(501), 1));
        var fire = Assert.Throws<InvariantViolationException>(() => h.Step());
        Assert.Contains("sent 1 weapon fires, the RPR federate received 0", fire.Message);

        h = WithRtiChecks();
        h.Checker.OnRprSent(new RprMunitionDetonation(Guid.NewGuid(), null, null, default, T.Dis(501), RprDetonationResult.GroundImpact));
        var detonation = Assert.Throws<InvariantViolationException>(() => h.Step());
        Assert.Contains("sent 1 detonations, the RPR federate received 0", detonation.Message);
    }

    [Fact]
    public void G9_detects_a_message_the_transport_neither_carried_nor_counted_as_dropped()
    {
        var h = WithRtiChecks();

        h.Checker.OnRprSent(new RprSimulationControl(RprControlKind.StartResume)); // never handed to the transport
        var ex = Assert.Throws<InvariantViolationException>(() => h.Step());

        Assert.Contains("simulation controls sent 1 / dropped 0", ex.Message);
    }

    [Fact]
    public void G9_accepts_a_simulation_control_the_transport_counted_as_dropped()
    {
        var h = WithRtiChecks();
        IRprMessage control = new RprSimulationControl(RprControlKind.StopFreeze);

        h.RprOut.Send(in control); // the recorder's tap is the checker; the transport is not behind it here
        h.Host.RprConnection.Send(in control);
        h.Step();

        Assert.Equal(1, h.Host.RprConnection.DroppedSimulationControl);
    }

    [Fact]
    public void G9_is_off_unless_asked_for()
    {
        var h = new GatewayHarness();

        h.Checker.OnRprSent(new RprSimulationControl(RprControlKind.StartResume)); // nothing behind it: neither sent nor dropped
        h.Step(); // no exception: the harness tests that forge messages are not subject to G-9

        Assert.Null(h.Checker.SentState(new RprObjectHandle(0x5555)));
    }
}
