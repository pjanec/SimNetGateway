using CycloneDDS.Runtime;
using SimNetGateway.Engine;
using SimNetGateway.Gateway.HrotDis;
using SimNetGateway.Protocol.DdsPlayer;

namespace SimNetGateway.Plugin.DdsPlayer;

/// <summary>One outbound item of a DPS topic: a sample to write, or (<see cref="Dispose"/>) the instance of the key in <see cref="Value"/> to dispose.</summary>
public readonly record struct DpsItem<T>(T Value, bool Dispose = false)
    where T : struct;

/// <summary>The writer of one topic as an outbound port channel: every item is written at once (the engine ticks on the host's main thread).</summary>
internal sealed class DpsChannel<T> : IOutboundChannel<DpsItem<T>>, IDisposable
    where T : struct
{
    private readonly DdsWriter<T> _writer;
    private readonly Func<T, T>? _prepare;
    private readonly Action<T, bool>? _observe;

    public DpsChannel(DdsParticipant participant, Func<T, T>? prepare = null, Action<T, bool>? observe = null)
    {
        _writer = new DdsWriter<T>(participant);
        _prepare = prepare;
        _observe = observe;
    }

    public int Written { get; private set; }

    public void Send(in DpsItem<T> item)
    {
        if (item.Dispose)
        {
            _observe?.Invoke(item.Value, true);
            _writer.DisposeInstance(item.Value);
            return;
        }

        var value = _prepare is null ? item.Value : _prepare(item.Value);
        _observe?.Invoke(value, false);
        _writer.Write(value);
        Written++;
    }

    public void Write(T value) => _writer.Write(value);

    public void Dispose() => _writer.Dispose();
}

/// <summary>
/// The DDS side of the plugin for one gateway runtime: a participant in the station's partition and one writer per published topic. Disposing it ends every
/// instance the station published (the world boundary), so a client never keeps a stale entity. Main thread only.
/// </summary>
public sealed class DpsTransport : IDisposable
{
    private readonly DdsParticipant _participant;
    private readonly IPlayerStation? _station;
    private readonly int _epoch;
    private readonly Dictionary<int, Entity> _entities = new();
    private readonly List<IDisposable> _channels = new();
    private int _offersSeen = -1;

    public DpsTransport(DpsSettings settings, string partition, IPlayerStation? station, int epoch)
    {
        Partition = partition;
        _station = station;
        _epoch = epoch;
        _participant = new DdsParticipant((uint)settings.Domain, defaultPartition: partition);
        try
        {
            Session = Add(new DpsChannel<Session>(_participant, prepare: s =>
            {
                s.WorldEpoch = (uint)_epoch;
                s.StationName = partition.Length > 64 ? partition[..64] : partition;
                return s;
            }));
            EntityTopic = Add(new DpsChannel<Entity>(_participant, prepare: e =>
            {
                e.Playable = IsOffered(e.EntityId);
                return e;
            }, observe: (e, disposed) =>
            {
                if (disposed)
                {
                    _entities.Remove(e.EntityId);
                }
                else
                {
                    _entities[e.EntityId] = e;
                }
            }));
            EntityState = Add(new DpsChannel<EntityState>(_participant));
            Articulation = Add(new DpsChannel<Articulation>(_participant));
            PersonState = Add(new DpsChannel<PersonState>(_participant));
            Appearance = Add(new DpsChannel<Appearance>(_participant));
            Fire = Add(new DpsChannel<Fire>(_participant));
            Detonation = Add(new DpsChannel<Detonation>(_participant));
            Effect = Add(new DpsChannel<Effect>(_participant));
            VehicleLook = Add(new DpsChannel<VehicleLook>(_participant));
        }
        catch
        {
            Dispose();
            throw;
        }
    }

    /// <summary>The DDS partition every topic of this station is written in.</summary>
    public string Partition { get; }

    internal DpsChannel<Session> Session { get; } = null!;

    internal DpsChannel<Entity> EntityTopic { get; } = null!;

    internal DpsChannel<EntityState> EntityState { get; } = null!;

    internal DpsChannel<Articulation> Articulation { get; } = null!;

    internal DpsChannel<PersonState> PersonState { get; } = null!;

    internal DpsChannel<Appearance> Appearance { get; } = null!;

    internal DpsChannel<Fire> Fire { get; } = null!;

    internal DpsChannel<Detonation> Detonation { get; } = null!;

    internal DpsChannel<Effect> Effect { get; } = null!;

    internal DpsChannel<VehicleLook> VehicleLook { get; } = null!;

    /// <summary>Once per frame: an entity whose playable flag changed with the station's offers is written again.</summary>
    public void Pump()
    {
        if (_station is null)
        {
            return;
        }

        var offers = _station.Offers;
        int hash = offers.Count;
        foreach (var o in offers)
        {
            hash = HashCode.Combine(hash, o.UnitId);
        }

        if (hash == _offersSeen)
        {
            return;
        }

        _offersSeen = hash;
        foreach (var (id, entity) in _entities.ToArray())
        {
            if (entity.Playable != IsOffered(id))
            {
                EntityTopic.Send(new DpsItem<Entity>(entity));
            }
        }
    }

    private bool IsOffered(int id)
    {
        if (_station is null)
        {
            return false;
        }

        foreach (var o in _station.Offers)
        {
            if (o.UnitId == id)
            {
                return true;
            }
        }

        return false;
    }

    private DpsChannel<T> Add<T>(DpsChannel<T> channel)
        where T : struct
    {
        _channels.Add(channel);
        return channel;
    }

    public void Dispose()
    {
        foreach (var channel in _channels)
        {
            channel.Dispose();
        }

        _channels.Clear();
        _participant.Dispose();
    }
}
