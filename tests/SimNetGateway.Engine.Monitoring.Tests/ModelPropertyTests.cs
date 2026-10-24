using SimNetGateway.Engine;

namespace SimNetGateway.Engine.Monitoring.Tests;

/// <summary>
/// Every model equals a brute-force computation from the engine after every tick of seeded random runs of the test world, also with
/// the journal and the event log off (R-070) or tiny, so that the engine recycles what the models copied.
/// </summary>
public class ModelPropertyTests
{
    public static TheoryData<int, int, int> Configurations()
    {
        var data = new TheoryData<int, int, int>();
        foreach (int seed in new[] { 1, 2, 3 })
        {
            data.Add(seed, 256, 64);   // roomy
            data.Add(seed, 0, 0);      // history off
            data.Add(seed, 3, 2);      // tiny
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(Configurations))]
    public void The_models_equal_the_oracle_after_every_tick(int seed, int journal, int events)
    {
        var world = new World(new GatewayOptions { JournalCapacity = journal, EventLogCapacity = events, DiagnosticsCapacity = 64 }, seed);
        var options = new EntityTableOptions { NameAttribute = world.Name, ParentAttribute = world.Parent };
        Func<IIdentityMap, AllocatorInfo?> hook = m => m.Name == "a.id" ? new AllocatorInfo("range", m.Count, 100 - m.Count, 0, 100) : null;
        var models = new AllModels(world.Engine, options, world.Mirror, seed, hook);
        int entitiesSeen = 0;
        for (int tick = 1; tick <= 300; tick++)
        {
            world.RandomTick();
            models.RefreshAndCheck($"seed {seed} journal {journal} events {events} tick {tick}");
            entitiesSeen = Math.Max(entitiesSeen, world.Engine.State.Count);
        }

        Assert.True(entitiesSeen > 15, "the run should build a population");
        Assert.True(world.Engine.Diagnostics.TransactionFailures > 0, "the run should include failing transactions");
    }

    [Fact]
    public void The_run_exercises_the_cases_the_properties_are_about()
    {
        var world = new World(seed: 4);
        int rejecting = 0, failing = 0, disabledSeen = 0;
        for (int tick = 1; tick <= 400; tick++)
        {
            world.RandomTick();
        }

        foreach (var rule in world.Engine.Rules)
        {
            rejecting += rule.ConditionRejects > 0 ? 1 : 0;
            failing += rule.Failures > 0 ? 1 : 0;
        }

        disabledSeen += world.Engine.FindRule("W/EvOff")!.FireCount == 0 ? 1 : 0;
        Assert.True(rejecting >= 2 && failing >= 2 && disabledSeen == 1);
        Assert.True(world.Engine.Diagnostics.SessionRejections > 0, "refused session commands");
        Assert.True(world.Mirror.Events.Count > 10);
        Assert.True(world.Engine.Identities.All.All(m => m.Count > 0));
        Assert.True(world.Slots.Count > 0 && world.Pending.Count > 0);
    }
}
