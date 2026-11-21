namespace SimNetGateway.Gateway.HrotDis;

/// <summary>
/// World readiness of the linked clients for every load (DD-022 3g): the host's load step calls <see cref="BeginWorld"/> at its commit and then polls <see cref="IsWorldReady"/>.
/// Two calls, so a poll right after the commit cannot read the OLD world as ready: <see cref="BeginWorld"/> forces a new world epoch, and nothing is ready before that epoch
/// is published. A host reaches it through the plugin's host service (<c>HostServices.Of("dps") as IClientWorldGate</c>). Main thread only.
/// </summary>
public interface IClientWorldGate
{
    /// <summary>
    /// A world-changing load commits: the station bumps its world epoch (even when it detects no boundary itself), publishes it on its next frame and remembers
    /// <paramref name="loadId"/> against it. Calling it again with the same id has no further effect.
    /// </summary>
    void BeginWorld(Guid loadId);

    /// <summary>
    /// True when the epoch of <paramref name="loadId"/> has been published and every live client that wants to be waited for reports Ready for the NEWEST published epoch (a world boundary after the commit moves the clients on to a newer epoch, and being ready for the world now shown is what the load needs); true at
    /// once when no such client exists (once the epoch is published); true for an id <see cref="BeginWorld"/> never saw. False before the epoch is published.
    /// <paramref name="failure"/> is the reason of the first client that reports Failed for the newest published epoch, <paramref name="step"/> the step of the first client still Loading it.
    /// </summary>
    bool IsWorldReady(Guid loadId, out string? failure, out string? step);
}
