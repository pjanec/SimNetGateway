using DISnet;
using SimNetGateway.Common;
using SimNetGateway.Engine;
using SimNetGateway.Gateway.HrotDis;
using SimNetGateway.Protocol.Dis7;
using SimNetGateway.Protocol.Hrot;
using static SimNetGateway.Gateway.HrotDis.Tests.DisPdus;
using static SimNetGateway.Gateway.HrotDis.Tests.Support;

namespace SimNetGateway.Gateway.HrotDis.Tests;

/// <summary>DIS entities in the gateway (H2b ingress, DD-006 6.2 and 7): Entity State and Remove Entity PDUs into the pivot, the echo and exercise filters, and the way on to HROT as replicas.</summary>
public class DisIngressTests
{
    private static readonly DisEntityKey Key5 = new(PeerSite, PeerApplication, 5);

    private static HrotDisHarness Live(GatewayConfiguration? config = null)
    {
        var h = new HrotDisHarness(config);
        h.GoLive();
        return h;
    }

    private static EntityRecord? Pivot(HrotDisHarness h, DisEntityKey key) =>
        h.DisIds!.TryResolve(key, out var id) ? h.Engine.State.Find(id) : null;

    private static Vec3 Ecef(HrotDisHarness h, double e, double n, double u) => h.Frame!.PointToEcef(new Vec3(e, n, u));

    private static int DisEntityCount(HrotDisHarness h) => h.Entities(h.DisSource).Count();

    [Fact]
    public void An_entity_state_pdu_creates_a_pivot_entity_of_origin_dis_with_the_pdu_values()
    {
        var h = Live();
        long type = new DisTypeCode(1, 2, 71, 5, 3, 2, 1).Pack();
        var position = Ecef(h, 300, 400, 5);
        var pdu = EntityState(5, position, new Vec3(0.5, -0.1, 0.2), new Vec3(3, 4, 5), new Vec3(0.1, 0.2, 0.3), new Vec3(0.01, 0.02, 0.03),
            algorithm: 4, damage: 2, marking: "PEER-5", force: 3, type: type);
        h.DisStep(pdu);

        var e = Pivot(h, Key5)!;
        Assert.NotNull(e);
        Assert.Equal(h.DisSource, e.Origin);
        var s = h.Schema;
        Assert.Equal(type, e.Get(s.EntityType).AsInt());
        Assert.Equal("PEER-5", e.Get(s.Marking).AsString());
        Assert.Equal(3, e.Get(s.Force).AsInt());
        Assert.Equal(position, e.Get(s.Position).AsVec3());
        Assert.Equal((double)(float)0.5, e.Get(s.Orientation).AsVec3().X);
        Assert.Equal((double)(float)-0.1, e.Get(s.Orientation).AsVec3().Y);
        Assert.Equal(new Vec3((float)3, (float)4, (float)5), e.Get(s.Velocity).AsVec3());
        Assert.Equal(new Vec3((float)0.1, (float)0.2, (float)0.3), e.Get(s.Acceleration).AsVec3());
        Assert.Equal(new Vec3((float)0.01, (float)0.02, (float)0.03), e.Get(s.AngularVelocity).AsVec3());
        Assert.Equal(4, e.Get(s.DrAlgorithm).AsInt());
        Assert.Equal(2, e.Get(s.Damage).AsInt());
        Assert.False(e.Has(s.Deactivated));
        Assert.True(h.DisIds!.TryGetKey(e.Id, out var bound));
        Assert.Equal(Key5, bound);
    }

    [Fact]
    public void The_state_time_is_the_engine_clock_at_receipt()
    {
        var h = Live();
        h.Run(2);
        double receipt = h.Clock.Now + HrotDisHarness.Dt - 0.04;
        h.DisStep(EntityState(5), receipt);
        Assert.Equal(receipt, Pivot(h, Key5)!.Get(h.Schema.StateTime).AsDouble());

        h.Run(1);
        h.DisStep(EntityState(5)); // no receipt time given: the clock of the tick
        Assert.Equal(h.Clock.Now, Pivot(h, Key5)!.Get(h.Schema.StateTime).AsDouble());
    }

    [Fact]
    public void A_second_pdu_with_the_same_id_updates_the_same_entity()
    {
        var h = Live();
        h.DisStep(EntityState(5, Ecef(h, 0, 0, 0), marking: "A"));
        var id = h.DisIds!.TryResolve(Key5, out var first) ? first : default;
        h.DisStep(EntityState(5, Ecef(h, 10, 0, 0), marking: "B", damage: 3));
        Assert.Equal(1, DisEntityCount(h));
        h.DisIds.TryResolve(Key5, out var second);
        Assert.Equal(id, second);
        Assert.Equal("B", Pivot(h, Key5)!.Get(h.Schema.Marking).AsString());
        Assert.Equal(3, Pivot(h, Key5)!.Get(h.Schema.Damage).AsInt());
    }

    [Fact]
    public void Different_ids_are_different_entities_even_with_the_same_entity_number()
    {
        var h = Live();
        h.DisStep(EntityState(5, site: 5, application: 77));
        h.DisStep(EntityState(5, site: 5, application: 78));
        h.DisStep(EntityState(5, site: 6, application: 77));
        Assert.Equal(3, DisEntityCount(h));
    }

    [Fact]
    public void The_deactivated_appearance_removes_the_entity_and_creates_nothing()
    {
        var h = Live();
        h.DisStep(EntityState(5));
        Assert.NotNull(Pivot(h, Key5));
        h.DisStep(EntityState(5, deactivated: true));
        Assert.Null(Pivot(h, Key5));
        Assert.Equal(0, DisEntityCount(h));
        Assert.Equal(0, h.DisIds!.Count);

        h.DisStep(EntityState(9, deactivated: true)); // an unknown entity leaving: nothing to create
        Assert.Equal(0, DisEntityCount(h));
    }

    [Fact]
    public void A_pdu_with_the_gateways_own_site_and_application_is_ignored_even_when_it_carries_a_valid_state()
    {
        var h = Live();
        h.DisStep(EntityState(5, site: 1, application: 3001));
        Assert.Equal(0, DisEntityCount(h));
        Assert.Equal(1, h.Services.DisIngress.Echoes);
    }

    [Fact]
    public void The_gateways_own_entity_state_pdus_coming_back_are_ignored()
    {
        var h = Live();
        h.HrotStep(HrotDisHarness.State(1, new HrotVec3(10, 10, 0)));
        var own = h.DisChecker!.States.Single().Pdu;
        h.DisStep(own); // the looped-back broadcast
        h.DisStep(DisPdus.ThroughTheWire(own));
        Assert.Equal(0, DisEntityCount(h));
        Assert.Equal(2, h.Services.DisIngress.Echoes);
        Assert.Single(h.Entities(h.HrotSource));
    }

    [Fact]
    public void A_pdu_of_another_exercise_is_ignored()
    {
        var h = Live();
        h.DisStep(EntityState(5, exercise: 2));
        Assert.Equal(0, DisEntityCount(h));
        Assert.Equal(1, h.Services.DisIngress.WrongExercise);
    }

    [Fact]
    public void A_pdu_with_a_value_that_is_not_a_number_changes_nothing()
    {
        var h = Live();
        var pdu = EntityState(5, Ecef(h, 1, 2, 3));
        pdu.EntityLocation.X = double.NaN; // not through the wire: the codec would carry the NaN, the ingress must refuse it
        h.DisStep(pdu);
        Assert.Equal(0, DisEntityCount(h));
        Assert.Equal(1, h.Engine.Diagnostics.TransactionFailures);

        var bad = EntityState(6, Ecef(h, 1, 2, 3));
        bad.DeadReckoningParameters.EntityAngularVelocity.Y = float.PositiveInfinity;
        h.DisStep(bad);
        Assert.Equal(0, DisEntityCount(h));
        Assert.Equal(2, h.Engine.Diagnostics.TransactionFailures);
    }

    [Theory]
    [InlineData(0, 1)]
    [InlineData(1, 1)]
    [InlineData(2, 2)]
    [InlineData(9, 9)]
    [InlineData(10, 1)]
    [InlineData(255, 1)]
    public void A_dead_reckoning_algorithm_without_a_predictor_is_read_as_static(int sent, int stored)
    {
        var h = Live();
        h.DisStep(EntityState(5, algorithm: sent));
        Assert.Equal(stored, Pivot(h, Key5)!.Get(h.Schema.DrAlgorithm).AsInt());
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public void The_damage_bits_of_the_appearance_become_the_damage_state(int damage)
    {
        var h = Live();
        h.DisStep(EntityState(5, damage: damage));
        Assert.Equal(damage, Pivot(h, Key5)!.Get(h.Schema.Damage).AsInt());
    }

    [Fact]
    public void A_marking_with_a_non_printable_character_is_read_as_a_question_mark()
    {
        var h = Live();
        var pdu = EntityState(5, marking: "AB");
        pdu.Marking.Characters[2] = 0x07;
        pdu.Marking.Characters[3] = (byte)'Z';
        h.DisStep(pdu);
        Assert.Equal("AB?Z", Pivot(h, Key5)!.Get(h.Schema.Marking).AsString());
    }

    [Fact]
    public void A_dis_entity_becomes_a_replica_for_hrot_with_the_ecef_values_in_the_local_frame()
    {
        var h = Live();
        h.DisStep(EntityState(5, Ecef(h, 120, -30, 4), velocity: h.Frame!.VectorToEcef(new Vec3(2, 3, 0)), marking: "PEER-5", force: 2, damage: 1));
        var replica = Assert.Single(h.Sent<ReplicaState>());
        Support.Near(new Vec3(120, -30, 4), replica.State.Position, 1e-6, "replica position");
        Support.Near(new Vec3(2, 3, 0), replica.State.Velocity, 1e-4, "replica velocity");
        Assert.Equal("PEER-5", replica.Marking);
        Assert.Equal(2, replica.Force);
        Assert.Equal(DrAlgorithms.Fpw, replica.State.DrAlgorithm);
        Assert.Equal(h.Clock.Now, replica.State.StateTime);
        Assert.Equal(75.0, replica.HealthCurrent); // slight damage = 75 % of 100
        Assert.False(replica.Deactivated);

        h.DisStep(EntityState(5, Ecef(h, 121, -30, 4), deactivated: true));
        var removed = Assert.Single(h.Sent<ReplicaRemoved>());
        Assert.Equal(replica.Id, removed.Id);
    }

    [Fact]
    public void Random_entity_state_pdus_become_consistent_pivot_entities_and_replica_states()
    {
        var random = new Random(4711);
        var h = Live();
        for (int i = 0; i < 600; i++)
        {
            ushort number = (ushort)(1 + random.Next(50));
            var enu = new Vec3(random.NextDouble() * 8000 - 4000, random.NextDouble() * 8000 - 4000, random.NextDouble() * 500);
            var velocityEnu = new Vec3(random.NextDouble() * 60 - 30, random.NextDouble() * 60 - 30, random.NextDouble() * 6 - 3);
            var euler = new Vec3(random.NextDouble() * 6.0 - 3.0, random.NextDouble() * 2.6 - 1.3, random.NextDouble() * 6.0 - 3.0);
            int algorithm = random.Next(1, 4); // world-axis algorithms: velocity in ECEF
            int damage = random.Next(4);
            string marking = "R" + random.Next(100000);
            byte force = (byte)random.Next(4);
            var ecef = h.Frame!.PointToEcef(enu);
            var velocity = h.Frame.VectorToEcef(velocityEnu);
            var angular = new Vec3(random.NextDouble() - 0.5, random.NextDouble() - 0.5, random.NextDouble() - 0.5);
            var pdu = EntityState(number, ecef, euler, velocity, default, angular, algorithm, damage, marking: marking, force: force);
            int before = h.Sent<ReplicaState>().Count;
            h.DisStep(pdu);

            var key = new DisEntityKey(PeerSite, PeerApplication, number);
            var e = Pivot(h, key)!;
            var s = h.Schema;
            Assert.Equal(ecef, e.Get(s.Position).AsVec3());
            Assert.Equal(new Vec3(pdu.EntityOrientation.Psi, pdu.EntityOrientation.Theta, pdu.EntityOrientation.Phi), e.Get(s.Orientation).AsVec3());
            Assert.Equal(new Vec3(pdu.EntityLinearVelocity.X, pdu.EntityLinearVelocity.Y, pdu.EntityLinearVelocity.Z), e.Get(s.Velocity).AsVec3());
            Assert.Equal(algorithm, e.Get(s.DrAlgorithm).AsInt());
            Assert.Equal(damage, e.Get(s.Damage).AsInt());
            Assert.Equal(marking, e.Get(s.Marking).AsString());
            Assert.Equal(force, e.Get(s.Force).AsInt());
            Assert.Equal(h.Clock.Now, e.Get(s.StateTime).AsDouble());

            var replicas = h.Sent<ReplicaState>();
            Assert.Equal(before + 1, replicas.Count); // exactly one replica state per PDU
            var replica = replicas[^1];
            Assert.True(h.HrotIds.TryGetKey(e.Id, out long hrotId));
            Assert.Equal(hrotId, replica.Id);
            Support.Near(h.Frame.PointFromEcef(ecef), replica.State.Position, 1e-6, "replica position");
            Support.Near(h.Frame.VectorFromEcef(new Vec3(pdu.EntityLinearVelocity.X, pdu.EntityLinearVelocity.Y, pdu.EntityLinearVelocity.Z)),
                replica.State.Velocity, 1e-6, "replica velocity");
            Assert.Equal(algorithm, replica.State.DrAlgorithm);
            Assert.Equal(marking, replica.Marking);
            Assert.Equal(force, replica.Force);
            Assert.Equal(h.Clock.Now, replica.State.StateTime);
            // the rotation of the replica is the same attitude as the PDU's Euler angles
            var back = LocalToGeocentricOrientation.ToDisEuler(GeoFrame.ToQuaternion(replica.State.Rotation), h.Frame);
            Assert.True(DisInvariantChecker.AngleBetween(back, e.Get(s.Orientation).AsVec3()) < 1e-6, "replica attitude");
        }

        Assert.Equal(50, DisEntityCount(h));
    }

    [Fact]
    public void A_remove_entity_pdu_destroys_the_named_dis_entity()
    {
        var h = Live();
        h.DisStep(EntityState(5));
        h.DisStep(EntityState(6));
        h.DisStep(RemoveEntity(Key5));
        Assert.Null(Pivot(h, Key5));
        Assert.NotNull(Pivot(h, new DisEntityKey(PeerSite, PeerApplication, 6)));
        Assert.Single(h.Sent<ReplicaRemoved>());
    }

    [Fact]
    public void A_remove_entity_pdu_cannot_remove_an_entity_the_gateway_published_for_hrot()
    {
        var h = Live();
        h.HrotStep(HrotDisHarness.State(1, new HrotVec3(1, 1, 1)));
        var key = h.DisChecker!.States.Single().Pdu.EntityID;
        h.DisStep(RemoveEntity(DisConversions.KeyOf(key)));
        Assert.Single(h.Entities(h.HrotSource));
        Assert.Equal(1, h.Services.DisIngress.RemovesIgnored);
        h.DisStep(RemoveEntity(new DisEntityKey(PeerSite, PeerApplication, 999))); // unknown
        Assert.Equal(2, h.Services.DisIngress.RemovesIgnored);
    }

    [Fact]
    public void A_remove_entity_pdu_from_the_gateway_itself_is_ignored()
    {
        var h = Live();
        h.DisStep(EntityState(5));
        h.DisStep(RemoveEntity(Key5, site: 1, application: 3001));
        Assert.NotNull(Pivot(h, Key5));
        Assert.Equal(1, h.Services.DisIngress.Echoes);
    }

    [Fact]
    public void Dis_entities_are_accepted_while_the_session_is_not_open_and_shown_to_hrot_when_it_opens()
    {
        var h = new HrotDisHarness();
        h.Origin(50, 14, 300);
        h.Session(ClusterPhase.Edit);
        h.DisStep(EntityState(5, Ecef(h, 1, 2, 3)));
        Assert.NotNull(Pivot(h, Key5));
        Assert.Single(h.Sent<ReplicaState>()); // Edit shows DIS replicas
    }

    [Fact]
    public void The_dis_entity_map_has_no_binding_for_the_hrot_entities_before_they_are_published()
    {
        var h = new HrotDisHarness();
        h.Origin(50, 14, 300);
        h.Session(ClusterPhase.Edit);
        h.HrotStep(HrotDisHarness.State(1, new HrotVec3(1, 2, 3)));
        Assert.Equal(0, h.DisIds!.Count);
        h.Session(ClusterPhase.Live);
        Assert.Equal(1, h.DisIds.Count);
    }
}

