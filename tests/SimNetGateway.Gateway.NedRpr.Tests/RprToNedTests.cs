using SimNetGateway.Engine;
using SimNetGateway.Protocol.Display;
using SimNetGateway.Protocol.Ned;
using SimNetGateway.Protocol.Rpr;

namespace SimNetGateway.Gateway.NedRpr.Tests;

/// <summary>DD-001 section 12, "End to end NED &lt;-&gt; RPR": the RPR to NED direction, echo suppression, control and staleness.</summary>
public class RprToNedTests
{
    private const uint Wolf = 0x9001;
    private const uint Fox = 0x9002;

    private static void AssertGeo(NedGeoSpatial actual, ulong id, double lat, double lon, double alt, double heading, double speed)
    {
        Assert.Equal(id, actual.Id);
        Assert.Equal(lat, actual.LatitudeDeg, 9);
        Assert.Equal(lon, actual.LongitudeDeg, 9);
        Assert.Equal(alt, actual.Altitude, 5);
        Assert.Equal(heading, actual.HeadingDeg, 6);
        Assert.Equal(0.0, actual.PitchDeg, 6);
        Assert.Equal(0.0, actual.RollDeg, 6);
        Assert.Equal(speed, actual.Speed, 6);
    }

    private static GatewayConfiguration WithSettings(NedRprSettings settings) => NedRprConfigurations.Default with { Settings = settings };

    [Fact]
    public void An_RPR_entity_reaches_NED_with_master_and_geo_spatial_under_a_gateway_range_id_and_is_never_echoed_to_RPR()
    {
        var h = new GatewayHarness();

        h.RprStep(T.RprFull(Wolf, "Wolf", 400, 50.2, 14.2, 200, 270, 20, 5));

        Assert.Equal(2, h.NedOut.Count);
        var master = Assert.IsType<NedEntityMaster>(h.NedOut.Messages[0].Message);
        var geo = Assert.IsType<NedGeoSpatial>(h.NedOut.Messages[1].Message);
        Assert.InRange(master.Id, 1_000_000UL, 1_999_999UL);
        Assert.Equal("Wolf", master.Name);
        Assert.Equal(400, master.TkbType);
        Assert.Equal(NedForce.Opposing, master.Force);
        AssertGeo(geo, master.Id, 50.2, 14.2, 200, 270, 20);
        Assert.Equal(0, h.RprOut.Count);
        h.Run(2);
        Assert.Equal(0, h.RprOut.Count);
        Assert.Equal(2, h.NedOut.Count);

        // The received DIS identifier is bound, and the entity is RPR-origin.
        var entity = h.EntityOfHandle(new RprObjectHandle(Wolf))!;
        Assert.Equal(h.RprSource, entity.Origin);
        Assert.True(h.RprEntityIds.TryGetKey(entity.Id, out var dis));
        Assert.Equal(new DisEntityId(9, 9, 5), dis);
        Assert.True(h.NedIds.TryResolve(master.Id, out var resolved));
        Assert.Equal(entity.Id, resolved);
    }

    [Fact]
    public void Later_RPR_changes_reach_NED_as_the_matching_descriptor_only()
    {
        var h = new GatewayHarness();
        h.RprStep(T.RprFull(Wolf, "Wolf", 400, 50.2, 14.2, 200, 270, 20, 5));
        ulong id = Assert.IsType<NedEntityMaster>(h.NedOut.Messages[0].Message).Id;
        h.ClearOutput();

        // Movement: geo-spatial only.
        NedRprConversions.GeoToPivot(50.21, 14.21, 200, 180, 0, 0, 25, out var p, out var o, out var v);
        h.RprStep(new RprObjectUpdate(new RprObjectHandle(Wolf))
        {
            WorldLocation = NedRprConversions.ToWorldLocation(p),
            Orientation = NedRprConversions.ToRprOrientation(o),
            VelocityVector = NedRprConversions.ToRprVector(v),
        });
        var geo = Assert.IsType<NedGeoSpatial>(Assert.Single(h.NedOut.Messages).Message);
        AssertGeo(geo, id, 50.21, 14.21, 200, 180, 25);
        h.ClearOutput();

        // Damage: the descriptor only.
        h.RprStep(new RprObjectUpdate(new RprObjectHandle(Wolf)) { DamageState = RprDamageState.SlightDamage });
        Assert.Equal(new NedEntityDamage(id, 1), Assert.IsType<NedEntityDamage>(Assert.Single(h.NedOut.Messages).Message));
        h.ClearOutput();

        // Rename: the master only.
        h.RprStep(new RprObjectUpdate(new RprObjectHandle(Wolf)) { Marking = "Wolf-1" });
        Assert.Equal(new NedEntityMaster(id, "Wolf-1", 400, NedForce.Opposing),
            Assert.IsType<NedEntityMaster>(Assert.Single(h.NedOut.Messages).Message));
        h.ClearOutput();

        // The same update again changes nothing: nothing is sent.
        h.RprStep(new RprObjectUpdate(new RprObjectHandle(Wolf)) { Marking = "Wolf-1", DamageState = RprDamageState.SlightDamage });
        Assert.Equal(0, h.NedOut.Count);
        Assert.Equal(0, h.RprOut.Count);
    }

    [Fact]
    public void An_RPR_removal_reaches_NED_as_a_dispose_and_an_unknown_removal_is_ignored()
    {
        var h = new GatewayHarness();
        h.RprStep(T.RprFull(Wolf, "Wolf", 400, 50.2, 14.2, 200, 270, 20, 5));
        ulong id = Assert.IsType<NedEntityMaster>(h.NedOut.Messages[0].Message).Id;
        h.ClearOutput();

        h.RprStep(new RprObjectRemoved(new RprObjectHandle(0xDEAD)));
        Assert.Equal(0, h.NedOut.Count);
        Assert.Equal(1, h.Engine.State.Count);

        h.RprStep(new RprObjectRemoved(new RprObjectHandle(Wolf)));

        Assert.Equal(new NedEntityDisposed(id), Assert.IsType<NedEntityDisposed>(Assert.Single(h.NedOut.Messages).Message));
        Assert.Equal(0, h.Engine.State.Count);
        Assert.False(h.NedIds.TryResolve(id, out _));
        Assert.Equal(0, h.RprOut.Count);
    }

    [Fact]
    public void Position_before_identity_reaches_NED_as_master_then_geo_spatial_once_the_identity_is_known()
    {
        var h = new GatewayHarness();
        NedRprConversions.GeoToPivot(50.2, 14.2, 200, 270, 0, 0, 20, out var p, out var o, out var v);
        h.RprStep(new RprObjectUpdate(new RprObjectHandle(Wolf))
        {
            WorldLocation = NedRprConversions.ToWorldLocation(p),
            Orientation = NedRprConversions.ToRprOrientation(o),
            VelocityVector = NedRprConversions.ToRprVector(v),
            DamageState = RprDamageState.ModerateDamage,
        });
        Assert.Equal(0, h.NedOut.Count); // NED needs a name and a type first

        h.RprStep(new RprObjectUpdate(new RprObjectHandle(Wolf)) { Marking = "Wolf", EntityType = T.Dis(400) });

        Assert.Equal(3, h.NedOut.Count);
        var master = Assert.IsType<NedEntityMaster>(h.NedOut.Messages[0].Message);
        AssertGeo(Assert.IsType<NedGeoSpatial>(h.NedOut.Messages[1].Message), master.Id, 50.2, 14.2, 200, 270, 20);
        Assert.Equal(new NedEntityDamage(master.Id, 2), Assert.IsType<NedEntityDamage>(h.NedOut.Messages[2].Message));
        Assert.Equal(0, h.RprOut.Count);
    }

    [Fact]
    public void A_looped_back_NED_publication_of_an_RPR_entity_is_ignored_and_nothing_is_sent()
    {
        var h = new GatewayHarness { LoopbackNed = true };

        h.RprStep(T.RprFull(Wolf, "Wolf", 400, 50.2, 14.2, 200, 270, 20, 5)); // Master + GeoSpatial loop back
        h.RprStep(new RprObjectUpdate(new RprObjectHandle(Wolf)) { DamageState = RprDamageState.Destroyed });
        h.Run(1);

        Assert.Equal(1, h.Engine.State.Count);
        Assert.Equal(1, h.NedIds.Count);
        Assert.Equal(3, h.NedOut.Count); // master, geo-spatial, damage - the loopback caused no answer
        Assert.Equal(0, h.RprOut.Count);
        Assert.Equal(0, h.Engine.Diagnostics.TransactionFailures);
        var entity = h.EntityOfHandle(new RprObjectHandle(Wolf))!;
        foreach (var attr in new[] { h.Schema.Name, h.Schema.TypeTkb, h.Schema.Force, h.Schema.Position, h.Schema.Orientation, h.Schema.Velocity })
        {
            Assert.True(entity.TryGetSlot(attr, out var slot));
            Assert.Equal(h.RprSource, slot.Meta.Source);
            Assert.Equal(1u, slot.Meta.Version);
        }

        Assert.Equal(h.RprSource, entity.Origin);
    }

    [Fact]
    public void NED_messages_that_name_a_gateway_allocated_id_never_change_an_RPR_entity()
    {
        var h = new GatewayHarness();
        h.RprStep(T.RprFull(Wolf, "Wolf", 400, 50.2, 14.2, 200, 270, 20, 5));
        ulong id = Assert.IsType<NedEntityMaster>(h.NedOut.Messages[0].Message).Id;
        h.ClearOutput();
        string before = h.Snapshot();

        h.NedStep(T.Master(id, "evil", 100, NedForce.Friendly));
        h.NedStep(T.Geo(id, lat: 10, lon: 10));
        h.NedStep(new NedEntityDamage(id, 3));
        h.NedStep(new NedWeaponStatus(id, 501, 99));
        h.NedStep(new NedWeaponFire(Guid.NewGuid(), id, 0, 501, 50, 14, 0, 1)); // a fire of "our" entity: our own fire looped back
        h.NedStep(new NedEntityDisposed(id));

        Assert.Equal(before, h.Snapshot());
        Assert.Equal(1, h.Engine.State.Count);
        Assert.Equal(0, h.NedOut.Count);
        Assert.Equal(0, h.RprOut.Count);
        Assert.Equal(0, h.Engine.Diagnostics.TransactionFailures);
    }

    [Fact]
    public void A_looped_back_RPR_publication_of_a_NED_entity_is_ignored_and_nothing_is_sent()
    {
        var h = new GatewayHarness { LoopbackRpr = true };
        var geo = T.Geo(T.Hawk, speed: 80);
        var entity = T.PublishNed(h, T.Hawk, "Hawk", 200, geo); // the first full update loops back
        h.NedStep(new NedEntityDamage(T.Hawk, 1));
        h.NedStep(geo with { Speed = 90 });
        h.Run(6.5); // a heartbeat loops back as well
        int sentBefore = h.RprOut.Count;

        var handle = h.HandleOf(entity);
        h.RprStep(new RprObjectUpdate(handle) { Marking = "evil", WorldLocation = new WorldLocation(1, 2, 3), DamageState = RprDamageState.Destroyed });
        h.RprStep(new RprObjectRemoved(handle));
        h.Run(1);

        Assert.Equal(sentBefore, h.RprOut.Count);
        Assert.Equal(1, h.Engine.State.Count);
        Assert.Equal("Hawk", entity.Get(h.Schema.Name).AsString());
        Assert.Equal(1, entity.Get(h.Schema.Damage).AsInt());
        Assert.Equal(h.NedSource, entity.Origin);
        foreach (var attr in new[] { h.Schema.Name, h.Schema.TypeTkb, h.Schema.Position, h.Schema.Damage })
        {
            Assert.True(entity.TryGetSlot(attr, out var slot));
            Assert.Equal(h.NedSource, slot.Meta.Source);
        }

        Assert.Equal(0, h.Engine.Diagnostics.TransactionFailures);
    }

    // ---- control ------------------------------------------------------------------------------------------------------

    [Fact]
    public void NED_control_drives_the_session_and_is_forwarded_to_RPR_but_never_back_to_NED()
    {
        var h = new GatewayHarness();
        var entity = T.PublishNed(h, T.Hawk, "Hawk");
        var hawkHandle = h.HandleOf(entity);
        h.ClearOutput();

        h.NedStep(new NedSimControl(NedControlKind.Load, "Ambush"));
        Assert.Equal(SessionState.Loaded, h.Engine.Session.State);
        Assert.Equal(SessionMode.Live, h.Engine.Session.Mode);
        Assert.Equal("Ambush", h.Engine.Session.Scenario);
        Assert.Equal(0.0, h.SimTime);
        Assert.Equal(1, h.Engine.State.Count); // free-play entities survive Load
        Assert.Equal(0, h.RprOut.Count); // nothing for Load
        h.Run(1);
        Assert.Equal(0.0, h.SimTime); // frozen until Start

        h.NedStep(new NedSimControl(NedControlKind.Start, null));
        Assert.Equal(SessionState.Running, h.Engine.Session.State);
        h.Run(1);
        Assert.True(h.SimTime > 0.9);

        h.NedStep(new NedSimControl(NedControlKind.Pause, null));
        Assert.Equal(SessionState.Paused, h.Engine.Session.State);
        double frozen = h.SimTime;
        h.Run(1);
        Assert.Equal(frozen, h.SimTime);

        h.NedStep(new NedSimControl(NedControlKind.Resume, null));
        Assert.Equal(SessionState.Running, h.Engine.Session.State);

        var controls = h.RprSent<RprSimulationControl>();
        Assert.Equal([RprControlKind.StartResume, RprControlKind.StopFreeze, RprControlKind.StartResume], controls.Select(c => c.Kind));
        Assert.Equal(3, h.RprOut.Count);
        Assert.Equal(0, h.NedOut.Count);

        h.NedStep(new NedSimControl(NedControlKind.Unload, null));
        Assert.Equal(SessionState.Idle, h.Engine.Session.State);
        Assert.Equal(0, h.Engine.State.Count);
        Assert.Equal(hawkHandle, Assert.Single(h.RprSent<RprObjectRemoved>()).Handle); // unload removes the object
        Assert.Equal(3, h.RprSent<RprSimulationControl>().Count); // Unload is not forwarded
        Assert.Equal(0, h.NedOut.Count);
    }

    [Fact]
    public void A_debrief_load_selects_replay_mode()
    {
        var h = new GatewayHarness();

        h.NedStep(new NedSimControl(NedControlKind.LoadDebrief, "Ambush-1"));

        Assert.Equal(SessionState.Loaded, h.Engine.Session.State);
        Assert.Equal(SessionMode.Replay, h.Engine.Session.Mode);
        Assert.Equal("Ambush-1", h.Engine.Session.Scenario);
    }

    [Fact]
    public void An_invalid_control_transition_is_rejected_and_not_forwarded()
    {
        var h = new GatewayHarness();
        h.Checker.Expect("invalid transition");

        h.NedStep(new NedSimControl(NedControlKind.Pause, null)); // Idle: nothing to pause
        h.NedStep(new NedSimControl(NedControlKind.Resume, null));

        Assert.Equal(SessionState.Idle, h.Engine.Session.State);
        Assert.Equal(0, h.RprOut.Count);
        Assert.Equal(2, h.Engine.Diagnostics.SessionRejections);
    }

    [Fact]
    public void Unload_removes_every_entity_on_both_sides()
    {
        var h = new GatewayHarness();
        var hawk = T.PublishNed(h, T.Hawk, "Hawk");
        var hawkHandle = h.HandleOf(hawk);
        h.RprStep(T.RprFull(Wolf, "Wolf", 400, 50.2, 14.2, 200, 270, 20, 5));
        ulong wolfNed = Assert.IsType<NedEntityMaster>(h.NedOut.Messages[0].Message).Id;
        h.Run(0.5);
        h.ClearOutput();

        h.NedStep(new NedSimControl(NedControlKind.Load, "x"));
        h.NedStep(new NedSimControl(NedControlKind.Unload, null));

        Assert.Equal(0, h.Engine.State.Count);
        Assert.Equal(hawkHandle, Assert.Single(h.RprSent<RprObjectRemoved>()).Handle);
        Assert.Equal(wolfNed, Assert.Single(h.NedSent<NedEntityDisposed>()).Id);
        Assert.Contains(h.DisplaySent<DisplayEntityRemoved>(), r => r.Label == "Hawk");
        Assert.Contains(h.DisplaySent<DisplayEntityRemoved>(), r => r.Label == "Wolf");
        Assert.Equal(0, h.NedIds.Count);
        Assert.Equal(0, h.RprHandles.Count);
        Assert.Equal(0, h.RprEntityIds.Count);
        Assert.Equal(0, h.DisplayLabels.Count);
    }

    // ---- staleness ----------------------------------------------------------------------------------------------------

    [Fact]
    public void A_silent_RPR_entity_is_reaped_after_twelve_seconds_not_before_even_while_the_DR_processor_keeps_predicting_it()
    {
        var h = new GatewayHarness();
        h.RprStep(T.RprFull(Wolf, "Wolf", 400, 50.2, 14.2, 200, 270, 20, 5));
        double lastWrite = h.WallTime;
        ulong id = Assert.IsType<NedEntityMaster>(h.NedOut.Messages[0].Message).Id;
        var entity = h.EntityOfHandle(new RprObjectHandle(Wolf))!;

        h.RunUntil(() => h.WallTime - lastWrite >= 11.9, 20);

        Assert.True(h.Engine.State.Find(entity.Id) is not null, "reaped before the timeout");
        Assert.DoesNotContain(h.NedOut.Messages, r => r.Message is NedEntityDisposed);
        // The prediction was still being written all the time (this is what R-068 is about).
        Assert.True(entity.TryGetSlot(h.Schema.PredictedPosition, out var predicted));
        Assert.Equal(h.Engine.Sources.Register("proc.dr"), predicted.Meta.Source);
        Assert.True(predicted.Meta.Version > 100, "the DR processor should have written about ten times a second");
        Assert.True(h.WallTime - predicted.Meta.WallTime < 0.2);

        h.RunUntil(() => h.Engine.State.Count == 0, 5);

        var disposed = h.NedOut.Messages.Single(r => r.Message is NedEntityDisposed);
        Assert.Equal(new NedEntityDisposed(id), disposed.Message);
        double age = disposed.WallTime - lastWrite;
        Assert.InRange(age, 12.0, 13.1);
        Assert.Contains(h.DisplayOut.Messages, r => r.Message is DisplayEntityRemoved { Label: "Wolf" });
        Assert.Equal(0, h.RprOut.Count);
        // The destruction was made by the reaper.
        var reaper = h.Engine.Sources.Register("proc.reaper");
        Assert.Contains(h.Records(), r => r.Source == reaper && r.Entities.Count == 1 && r.Entities[0].Destroyed);
    }

    [Fact]
    public void An_RPR_entity_that_keeps_sending_is_not_reaped_and_stops_living_twelve_seconds_after_its_last_message()
    {
        var h = new GatewayHarness();
        var update = T.RprFull(Wolf, "Wolf", 400, 50.2, 14.2, 200, 270, 20, 5);
        h.RprStep(update);
        for (int i = 0; i < 6; i++) // a refresh every 5 s for 30 s
        {
            h.Run(5);
            h.Rpr(update with { Marking = "Wolf" });
            h.Rpr(new RprObjectUpdate(update.Handle) { Marking = "Wolf" });
            NedRprConversions.GeoToPivot(50.2 + 0.001 * (i + 1), 14.2, 200, 270, 0, 0, 20, out var p, out _, out _);
            h.Rpr(new RprObjectUpdate(update.Handle) { WorldLocation = NedRprConversions.ToWorldLocation(p) });
            h.Step();
            Assert.Equal(1, h.Engine.State.Count);
        }

        double lastWrite = h.WallTime;
        h.RunUntil(() => h.Engine.State.Count == 0, 20);

        Assert.InRange(h.NedOut.Messages.Last().WallTime - lastWrite, 12.0, 13.1);
    }

    [Fact]
    public void The_reaper_touches_only_entities_of_its_own_side()
    {
        var h = new GatewayHarness();
        var hawk = T.PublishNed(h, T.Hawk, "Hawk");

        h.Run(40); // NED is silent far longer than the timeout; the heartbeat keeps the RPR object alive

        Assert.NotNull(h.Engine.State.Find(hawk.Id));
        Assert.Empty(h.RprSent<RprObjectRemoved>());
    }

    // ---- NED id range exhaustion ----------------------------------------------------------------------------------

    [Theory]
    [InlineData("position")]
    [InlineData("marking")]
    public void When_the_NED_id_range_is_exhausted_the_entity_is_not_published_and_is_published_after_an_id_is_freed(string retryBy)
    {
        var h = new GatewayHarness(WithSettings(new NedRprSettings { NedIdRange = new NedIdRange(5000, 5000) }));
        h.Checker.Expect("NED id range exhausted");

        h.RprStep(T.RprFull(Wolf, "Wolf", 400, 50.2, 14.2, 200, 270, 20, 5));
        Assert.Equal(5000UL, Assert.IsType<NedEntityMaster>(h.NedOut.Messages[0].Message).Id);
        h.ClearOutput();

        h.RprStep(T.RprFull(Fox, "Fox", 400, 50.3, 14.3, 200, 90, 10, 6)); // no id left
        Assert.Equal(0, h.NedOut.Count);
        Assert.Equal(1, h.Checker.WarningsContaining("NED id range exhausted"));
        Assert.Equal(2, h.Engine.State.Count);

        // More changes of the unpublished entity do not repeat the warning.
        h.RprStep(new RprObjectUpdate(new RprObjectHandle(Fox)) { DamageState = RprDamageState.SlightDamage });
        Assert.Equal(0, h.NedOut.Count);
        Assert.Equal(1, h.Checker.WarningsContaining("NED id range exhausted"));

        // Another change of the unpublished entity retries, fails again and stays quiet.
        NedRprConversions.GeoToPivot(50.305, 14.3, 200, 90, 0, 0, 10, out var again, out _, out _);
        h.RprStep(new RprObjectUpdate(new RprObjectHandle(Fox)) { WorldLocation = NedRprConversions.ToWorldLocation(again) });
        Assert.Equal(0, h.NedOut.Count);
        Assert.Equal(1, h.Checker.WarningsContaining("NED id range exhausted"));

        // The first entity goes away and frees its id.
        h.RprStep(new RprObjectRemoved(new RprObjectHandle(Wolf)));
        Assert.Equal(new NedEntityDisposed(5000), Assert.Single(h.NedOut.Messages).Message);
        h.ClearOutput();

        // The next change of the second entity publishes it.
        if (retryBy == "position")
        {
            NedRprConversions.GeoToPivot(50.31, 14.3, 200, 90, 0, 0, 10, out var p, out _, out _);
            h.RprStep(new RprObjectUpdate(new RprObjectHandle(Fox)) { WorldLocation = NedRprConversions.ToWorldLocation(p) });
        }
        else
        {
            h.RprStep(new RprObjectUpdate(new RprObjectHandle(Fox)) { Marking = "Fox-2" });
        }

        Assert.Equal(new NedEntityMaster(5000, retryBy == "marking" ? "Fox-2" : "Fox", 400, NedForce.Opposing),
            Assert.IsType<NedEntityMaster>(h.NedOut.Messages[0].Message));
        Assert.IsType<NedGeoSpatial>(h.NedOut.Messages[1].Message);
        Assert.IsType<NedEntityDamage>(h.NedOut.Messages[2].Message);
        Assert.Equal(3, h.NedOut.Count);
        Assert.Equal(1, h.Checker.WarningsContaining("NED id range exhausted"));
    }
}
