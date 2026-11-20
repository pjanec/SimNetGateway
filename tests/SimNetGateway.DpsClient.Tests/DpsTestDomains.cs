namespace SimNetGateway.DpsClient.Tests;

/// <summary>
/// The DDS domains of this assembly's tests: one counter for every test class (xUnit runs classes in parallel, and two tests on one domain meet each other's participants), in
/// the range 100..166 that no other test assembly uses. DDS uses UDP port 7400 + 250 * domain, and Windows excludes or hands out the ports above 49152 (so a domain from 167
/// up fails to create a participant at random): the ranges of all test assemblies stay below it.
/// </summary>
internal static class DpsTestDomains
{
    private static int _counter = Random.Shared.Next(0, 67);

    public static uint Next() => (uint)(100 + Interlocked.Increment(ref _counter) % 67);
}
