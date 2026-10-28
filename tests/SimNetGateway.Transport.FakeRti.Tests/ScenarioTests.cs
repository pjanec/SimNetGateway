namespace SimNetGateway.Transport.FakeRti.Tests;

/// <summary>
/// Success condition 5 of DD-008 section 8: random seeded scenarios (2-5 federates, 30-200 operations, every kind of
/// operation including illegal ones) are played against the fake RTI and against the brute-force reference model, and after
/// EVERY operation the exception thrown, the returned values, the callbacks delivered to each federate (in order, canonical
/// text), the callbacks still pending and the callback probe (every service refused from inside a callback) must agree.
/// </summary>
public class ScenarioTests
{
    private const int ScenarioCount = 400;

    private static readonly Oracle Handles = new();

    [Fact]
    public void Scenarios_agree_with_the_reference_model_after_every_operation()
    {
        var coverage = new Coverage();
        for (var seed = 1; seed <= ScenarioCount; seed++)
        {
            ScenarioRun.Play(Handles, seed, coverage);
        }

        Assert.Equal(ScenarioCount, coverage.Scenarios);
        Assert.True(coverage.Operations >= ScenarioCount * 30);
    }

    [Fact]
    public void The_generated_scenarios_exercise_what_they_claim_to()
    {
        var coverage = new Coverage();
        for (var seed = 1; seed <= ScenarioCount; seed++)
        {
            ScenarioRun.Play(Handles, seed, coverage);
        }

        // enough activity of every callback kind ...
        Assert.True(coverage.Discoveries > 1000, coverage.Summary());
        Assert.True(coverage.Promotions > 500, $"promotions {coverage.Promotions}");
        Assert.True(coverage.Reflections > 700, $"reflections {coverage.Reflections}");
        Assert.True(coverage.Removals > 150, $"removals {coverage.Removals}");
        Assert.True(coverage.Interactions > 300, $"interactions {coverage.Interactions}");
        Assert.True(coverage.Provides > 150, $"provides {coverage.Provides}");
        Assert.True(coverage.Reservations > 500, $"reservations {coverage.Reservations}");
        Assert.True(coverage.Callbacks > 3000, $"callbacks probed {coverage.Callbacks}");

        // ... every exception of the semantics table was provoked and agreed on ...
        string[] expected =
        [
            "NotConnected", "AlreadyConnected", "FederateNotExecutionMember", "FederationExecutionAlreadyExists",
            "FederationExecutionDoesNotExist", "FederateAlreadyExecutionMember", "FederateNameAlreadyInUse",
            "FederatesCurrentlyJoined", "FederateOwnsAttributes", "NameNotFound", "InconsistentFdd", "InvalidObjectClassHandle",
            "InvalidInteractionClassHandle", "ObjectClassNotDefined", "InteractionClassNotDefined", "AttributeNotDefined",
            "InteractionParameterNotDefined", "ObjectClassNotPublished", "InteractionClassNotPublished", "AttributeNotOwned",
            "ObjectInstanceNotKnown", "ObjectInstanceNameInUse", "ObjectInstanceNameNotReserved", "IllegalName",
            "DeletePrivilegeNotHeld",
        ];
        Assert.All(expected, name => Assert.Contains(name, coverage.Exceptions));

        // ... and every kind of operation succeeded at least once
        Assert.All(Enum.GetValues<K>(), kind => Assert.Contains(kind, coverage.SucceededKinds));
    }

    [Theory]
    [InlineData(1000001)]
    [InlineData(1000002)]
    [InlineData(1000003)]
    public void Another_seed_range_agrees_too(int firstSeed)
    {
        var coverage = new Coverage();
        for (var seed = firstSeed; seed < firstSeed + 25; seed++)
        {
            ScenarioRun.Play(Handles, seed, coverage);
        }
        Assert.Equal(25, coverage.Scenarios);
    }

    [Fact]
    public void A_scenario_is_a_function_of_its_seed()
    {
        var one = ScenarioRun.Play(Handles, 77, new Coverage());
        var two = ScenarioRun.Play(Handles, 77, new Coverage());

        Assert.Equal(one.Trace, two.Trace); // no randomness, no timing in the RTI: same calls, same callbacks, same exceptions
    }
}
