using SimNetGateway.Protocol.Rpr;

namespace SimNetGateway.Gateway.NedRpr.Tests;

public class SimulatedRtiTests
{
    private static List<RprObjectRegistered> Deliver(SimulatedRti rti, out int count)
    {
        var list = new List<RprObjectRegistered>();
        count = rti.DeliverPending(list.Add);
        return list;
    }

    [Fact]
    public void A_registration_is_delivered_by_the_next_call_exactly_once()
    {
        var rti = new SimulatedRti();
        rti.Observe(new RprRegisterObject(7));

        var first = Deliver(rti, out int n1);
        Assert.Equal(1, n1);
        Assert.Equal(new[] { new RprObjectRegistered(7, new RprObjectHandle(0x2A01)) }, first);

        var second = Deliver(rti, out int n2);
        Assert.Equal(0, n2);
        Assert.Empty(second);
    }

    [Fact]
    public void Nothing_is_delivered_without_a_request()
    {
        var rti = new SimulatedRti();
        var delivered = Deliver(rti, out int n);
        Assert.Equal(0, n);
        Assert.Empty(delivered);
        Assert.Empty(rti.Registered);
    }

    [Fact]
    public void A_registration_requested_while_delivering_waits_for_the_next_call()
    {
        var rti = new SimulatedRti();
        rti.Observe(new RprRegisterObject(1));
        var delivered = new List<RprObjectRegistered>();

        int n = rti.DeliverPending(r =>
        {
            delivered.Add(r);
            rti.Observe(new RprRegisterObject(2)); // requested after this call began
        });

        Assert.Equal(1, n);
        Assert.Equal(1L, Assert.Single(delivered).Token);

        var next = Deliver(rti, out int n2);
        Assert.Equal(1, n2);
        Assert.Equal(2L, Assert.Single(next).Token);
    }

    [Fact]
    public void Handles_are_unique_and_sequential_from_0x2A01_across_calls()
    {
        var rti = new SimulatedRti();
        var all = new List<RprObjectRegistered>();
        long token = 100;
        for (int round = 0; round < 5; round++)
        {
            for (int i = 0; i <= round; i++)
            {
                rti.Observe(new RprRegisterObject(token++));
            }

            all.AddRange(Deliver(rti, out _));
        }

        Assert.Equal(15, all.Count);
        for (int i = 0; i < all.Count; i++)
        {
            Assert.Equal(0x2A01u + (uint)i, all[i].Handle.Value);
        }

        Assert.Equal(all.Count, all.Select(r => r.Handle).Distinct().Count());
        Assert.Equal(all.Select(r => r.Handle), rti.Registered);
    }

    [Fact]
    public void Delivery_order_is_request_order_and_tokens_are_echoed()
    {
        var rti = new SimulatedRti();
        long[] tokens = { 50, 3, 9999999999L, -4, 12 };
        foreach (long t in tokens)
        {
            rti.Observe(new RprRegisterObject(t));
        }

        var delivered = Deliver(rti, out int n);
        Assert.Equal(tokens.Length, n);
        Assert.Equal(tokens, delivered.Select(r => r.Token));
    }

    [Fact]
    public void Non_registration_messages_are_ignored()
    {
        var rti = new SimulatedRti();
        var h = new RprObjectHandle(1);
        rti.Observe(new RprObjectUpdate(h));
        rti.Observe(new RprObjectRemoved(h));
        rti.Observe(new RprSimulationControl(RprControlKind.StartResume));
        rti.Observe(new RprWeaponFire(Guid.NewGuid(), null, null, default, default, 1));
        rti.Observe(new RprMunitionDetonation(Guid.NewGuid(), null, null, default, default, RprDetonationResult.Other));
        rti.Observe(new RprObjectRegistered(5, h));

        var delivered = Deliver(rti, out int n);
        Assert.Equal(0, n);
        Assert.Empty(delivered);
        Assert.Empty(rti.Registered);

        // and the handle counter was not consumed by them
        rti.Observe(new RprRegisterObject(1));
        Assert.Equal(0x2A01u, Deliver(rti, out _).Single().Handle.Value);
    }

    [Fact]
    public void Registered_grows_only_when_delivered()
    {
        var rti = new SimulatedRti();
        rti.Observe(new RprRegisterObject(1));
        Assert.Empty(rti.Registered);
        Deliver(rti, out _);
        Assert.Single(rti.Registered);
    }

    [Fact]
    public void A_null_callback_is_rejected()
    {
        Assert.Throws<ArgumentNullException>(() => new SimulatedRti().DeliverPending(null!));
    }
}
