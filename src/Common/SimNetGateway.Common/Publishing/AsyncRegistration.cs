using SimNetGateway.Engine;

namespace SimNetGateway.Common;

/// <summary>Where an entity is on its way to a protocol side that registers objects asynchronously.</summary>
public enum RegistrationStatus : byte
{
    /// <summary>Not registered (nothing requested, or released).</summary>
    None,

    /// <summary>Registration requested, no answer yet; the caller coalesces what it wants to send.</summary>
    Pending,

    /// <summary>The answer arrived and was confirmed; the entity has a handle.</summary>
    Bound,
}

/// <summary>What <see cref="AsyncRegistration{THandle}.OnRegistered"/> made of an answer.</summary>
public enum RegistrationAnswer : byte
{
    /// <summary>The token is not (or no longer) known: never issued, or already answered. Nothing was recorded.</summary>
    Unknown,

    /// <summary>The entity was released (removed) while its registration was pending: the handle is queued as an orphan, to be deleted again.</summary>
    Released,

    /// <summary>The token belongs to a live entity; the arrival is queued until the caller confirms the binding with <see cref="AsyncRegistration{THandle}.Complete"/>.</summary>
    Accepted,
}

/// <summary>
/// The asynchronous registration machine of a protocol side whose peer hands out the object handle in a later message (DD-009
/// section 3 item 2, R-115): None -> Pending -> Bound per entity, with tokens, the arrivals to confirm and the orphans to delete.
/// The caller sends the register message with the token <see cref="Request"/> returned, feeds the answer to
/// <see cref="OnRegistered"/>, binds the handle in its own identity map (inside the ingress transaction), and after the commit
/// confirms with <see cref="Complete"/>. It knows no protocol: <typeparamref name="THandle"/> is opaque. Invariants: a token is
/// issued once and never reused; an answer is accepted once; an answer for a released entity is never bound (it becomes an
/// orphan); an entity is requested again only after it was released.
/// </summary>
public sealed class AsyncRegistration<THandle>
    where THandle : notnull
{
    private struct Entry
    {
        public long Token;
        public RegistrationStatus Status;
        public bool Answered;
        public THandle Handle;
    }

    private readonly Dictionary<EntityId, Entry> _entries = new();
    private readonly Dictionary<long, EntityId> _awaiting = new();
    private readonly HashSet<long> _released = new();
    private readonly List<(EntityId Entity, THandle Handle)> _arrivals = new();
    private readonly List<THandle> _orphans = new();
    private long _lastToken;

    /// <summary>Entities whose registration awaits an answer or its confirmation.</summary>
    public int PendingCount { get; private set; }

    /// <summary>Entities with a handle.</summary>
    public int BoundCount { get; private set; }

    /// <summary>The arrivals accepted since the last <see cref="ClearArrivals"/>, in the order the answers came.</summary>
    public IReadOnlyList<(EntityId Entity, THandle Handle)> Arrivals => _arrivals;

    /// <summary>The handles of objects whose entity was released before the answer came, since the last <see cref="ClearOrphans"/>.</summary>
    public IReadOnlyList<THandle> Orphans => _orphans;

    public RegistrationStatus StatusOf(EntityId entity) =>
        _entries.TryGetValue(entity, out var entry) ? entry.Status : RegistrationStatus.None;

    /// <summary>The token of the pending registration of the entity; false when it has none.</summary>
    public bool TryGetToken(EntityId entity, out long token)
    {
        if (_entries.TryGetValue(entity, out var entry) && entry.Status == RegistrationStatus.Pending)
        {
            token = entry.Token;
            return true;
        }

        token = 0;
        return false;
    }

    /// <summary>The handle of a bound entity.</summary>
    public bool TryGetHandle(EntityId entity, out THandle handle)
    {
        if (_entries.TryGetValue(entity, out var entry) && entry.Status == RegistrationStatus.Bound)
        {
            handle = entry.Handle;
            return true;
        }

        handle = default!;
        return false;
    }

    /// <summary>
    /// Starts the registration of an entity that has none: it becomes Pending and the new token (1, 2, 3, ...) is returned. For an
    /// entity that is already Pending or Bound nothing happens and 0 is returned (0 is never a token): the caller coalesces.
    /// </summary>
    public long Request(EntityId entity)
    {
        if (_entries.ContainsKey(entity))
        {
            return 0;
        }

        long token = ++_lastToken;
        _entries[entity] = new Entry { Token = token, Status = RegistrationStatus.Pending };
        _awaiting[token] = entity;
        PendingCount++;
        return token;
    }

    /// <summary>
    /// The peer's answer to a registration. <see cref="RegistrationAnswer.Accepted"/>: the arrival is queued and the entity stays
    /// Pending until <see cref="Complete"/>; when <paramref name="isAlive"/> says the entity is gone, it is released and the handle
    /// becomes an orphan instead (<see cref="RegistrationAnswer.Released"/>). Unknown tokens, and a second answer to a token, change
    /// nothing.
    /// </summary>
    public RegistrationAnswer OnRegistered(long token, THandle handle, out EntityId entity, Func<EntityId, bool>? isAlive = null)
    {
        if (_awaiting.Remove(token, out entity))
        {
            if (isAlive is not null && !isAlive(entity))
            {
                _entries.Remove(entity);
                PendingCount--;
                _orphans.Add(handle);
                return RegistrationAnswer.Released;
            }

            var entry = _entries[entity];
            entry.Answered = true;
            _entries[entity] = entry;
            _arrivals.Add((entity, handle));
            return RegistrationAnswer.Accepted;
        }

        entity = default;
        if (_released.Remove(token))
        {
            _orphans.Add(handle);
            return RegistrationAnswer.Released;
        }

        return RegistrationAnswer.Unknown;
    }

    /// <summary>The caller bound the handle of an accepted arrival: the entity becomes Bound. False when it was released meanwhile, is not pending or its answer was not accepted.</summary>
    public bool Complete(EntityId entity, THandle handle)
    {
        if (!_entries.TryGetValue(entity, out var entry) || entry.Status != RegistrationStatus.Pending || !entry.Answered)
        {
            return false;
        }

        entry.Status = RegistrationStatus.Bound;
        entry.Handle = handle;
        _entries[entity] = entry;
        PendingCount--;
        BoundCount++;
        return true;
    }

    /// <summary>
    /// The entity is gone: its registration is forgotten. An answer that still comes for a pending token ends as
    /// <see cref="RegistrationAnswer.Released"/>. Releasing an entity with no registration does nothing.
    /// </summary>
    public void Release(EntityId entity)
    {
        if (!_entries.Remove(entity, out var entry))
        {
            return;
        }

        if (entry.Status == RegistrationStatus.Bound)
        {
            BoundCount--;
            return;
        }

        PendingCount--;
        if (_awaiting.Remove(entry.Token))
        {
            _released.Add(entry.Token);
        }
    }

    public void ClearArrivals() => _arrivals.Clear();

    public void ClearOrphans() => _orphans.Clear();
}
