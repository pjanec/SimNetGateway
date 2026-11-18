using SimNetGateway.Engine;

namespace SimNetGateway.HrotHost;

/// <summary>
/// The gateway's <c>hrot.entity</c> id allocator port over the node's id allocator (ids are <c>int</c>).
/// Main thread only (the engine ticks on the main thread). HROT's allocator has no release: a released id is simply not used again until the world
/// restarts its ids (a world boundary rebuilds the engine, so no stale mapping survives).
/// </summary>
public sealed class NodeIdAllocator : IIdAllocator<int>
{
    private readonly Func<int> _allocate;

    /// <param name="allocate">The node allocator's <c>AllocateId</c> (<c>context.IdAllocator.AllocateId</c>).</param>
    public NodeIdAllocator(Func<int> allocate) => _allocate = allocate ?? throw new ArgumentNullException(nameof(allocate));

    /// <summary>Ids handed out so far (diagnostics, tests).</summary>
    public int Allocated { get; private set; }

    public bool TryAllocate(out int key)
    {
        key = _allocate();
        if (key <= 0)
        {
            return false;
        }

        Allocated++;
        return true;
    }

    public void Release(in int key)
    {
    }
}
