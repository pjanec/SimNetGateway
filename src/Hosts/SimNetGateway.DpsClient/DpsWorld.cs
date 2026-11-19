using System.Diagnostics;
using System.Security.Cryptography;
using CycloneDDS.Runtime;
using SimNetGateway.Protocol.DdsPlayer;
using D = SimNetGateway.Protocol.DdsPlayer;

namespace SimNetGateway.DpsClient;

/// <summary>One mirrored entity: the latest <see cref="D.Entity"/> and <see cref="D.EntityState"/> and the client time the state arrived.</summary>
public sealed class DpsEntity
{
    public D.Entity? Info { get; internal set; }

    public D.EntityState? State { get; internal set; }

    /// <summary>The client clock (seconds) when <see cref="State"/> was received.</summary>
    public double ReceivedAt { get; internal set; }

    /// <summary>The position at client time <paramref name="now"/>, dead-reckoned from the last sample's velocity (x east, y north, z up).</summary>
    public D.Vec3d PositionAt(double now)
    {
        if (State is not { } s)
        {
            return default;
        }

        double dt = Math.Max(0.0, now - ReceivedAt);
        return new D.Vec3d
        {
            X = s.Position.X + s.LinearVelocity.X * dt,
            Y = s.Position.Y + s.LinearVelocity.Y * dt,
            Z = s.Position.Z + s.LinearVelocity.Z * dt,
        };
    }
}

/// <summary>
/// The reference client without a window: subscribes to the DPS topics of one station partition, keeps the latest state per entity, and writes the player topics
/// (join, control frames, leave, gizmo view). Main thread only; <see cref="Pump"/> once per frame. Free of raylib so it can be tested headless.
/// </summary>
public sealed class DpsWorld : IDisposable
{
    private readonly DdsParticipant _participant;
    private readonly Func<double> _clock;
    private readonly DdsReader<D.Session> _session;
    private readonly DdsReader<D.Entity> _entity;
    private readonly DdsReader<D.EntityState> _state;
    private readonly DdsReader<D.PlayerSlot> _slot;
    private readonly DdsReader<D.OwnUnit> _own;
    private readonly DdsReader<D.GizmoFrame> _gizmo;
    private readonly DdsWriter<D.JoinRequest> _join;
    private readonly DdsWriter<D.ControlFrame> _control;
    private readonly DdsWriter<D.Leave> _leave;
    private readonly DdsWriter<D.GizmoView> _view;
    private readonly Dictionary<(string Producer, string Layer), D.GizmoFrame> _gizmos = new();
    private readonly List<IDisposable> _counters = new();
    private D.ControlFrame _frame;
    private uint _joinSeq;
    private uint _leaveSeq;
    private D.GizmoView? _lastView;

    public DpsWorld(uint domain, string partition, ulong? clientId = null, Func<double>? clock = null)
    {
        var sw = Stopwatch.StartNew();
        _clock = clock ?? (() => sw.Elapsed.TotalSeconds);
        Partition = partition;
        ClientId = clientId is > 0 ? clientId.Value : RandomId();
        _participant = new DdsParticipant(domain, defaultPartition: partition);
        _session = new DdsReader<D.Session>(_participant);
        _entity = new DdsReader<D.Entity>(_participant);
        _state = new DdsReader<D.EntityState>(_participant);
        _slot = new DdsReader<D.PlayerSlot>(_participant);
        _own = new DdsReader<D.OwnUnit>(_participant);
        _gizmo = new DdsReader<D.GizmoFrame>(_participant);
        _join = new DdsWriter<D.JoinRequest>(_participant);
        _control = new DdsWriter<D.ControlFrame>(_participant);
        _leave = new DdsWriter<D.Leave>(_participant);
        _view = new DdsWriter<D.GizmoView>(_participant);
        Count<D.PersonState>(DpsProtocol.PersonState);
        Count<D.Appearance>(DpsProtocol.Appearance);
        Count<D.VehicleLook>(DpsProtocol.VehicleLook);
        Count<D.Fire>(DpsProtocol.Fire, f =>
        {
            if (Slot is { UnitId: > 0 } slot && f.ShooterId == slot.UnitId)
            {
                OwnFire++;
            }
        });
        Count<D.Detonation>(DpsProtocol.Detonation);
        Count<D.Effect>(DpsProtocol.Effect);
        Count<D.Articulation>(DpsProtocol.Articulation);
    }

    /// <summary>Samples received per topic since start (live samples only), for the topics the client does not otherwise keep.</summary>
    public Dictionary<string, int> Received { get; } = new()
    {
        [DpsProtocol.Entity] = 0, [DpsProtocol.EntityState] = 0,
    };

    private readonly List<Action> _countPumps = new();

    /// <summary>Fire samples whose shooter is the own unit.</summary>
    public int OwnFire { get; private set; }

    /// <summary>The <c>Session.WorldEpoch</c> of the world the client shows (0 before the first Session).</summary>
    public uint WorldEpoch { get; private set; }

    /// <summary>
    /// A new world (DD-017 3.11): the gateway's player and gizmo readers are new and remember nothing of this client, so the latest sim time starts over and the
    /// gizmo view is sent again; a player joins again through the slot (the automatic join does it while the slot is Choosing).
    /// </summary>
    private void OnNewWorld(uint epoch)
    {
        bool change = WorldEpoch != 0;
        WorldEpoch = epoch;
        if (!change)
        {
            return;
        }

        LatestSimTime = 0;
        if (_lastView is { } view)
        {
            _view.Write(view);
        }
    }

    /// <summary>The newest simulation time seen on any EntityState of the current world (0 before the first).</summary>
    public double LatestSimTime { get; private set; }

    /// <summary>True after <see cref="Leave"/> until the next explicit <see cref="JoinFirstUnit"/>: an automatic join stays quiet.</summary>
    public bool HasLeft { get; private set; }

    private void Count<T>(string topic, Action<T>? each = null)
        where T : struct
    {
        Received[topic] = 0;
        var reader = new DdsReader<T>(_participant);
        _counters.Add(reader);
        _countPumps.Add(() => Drain(reader, d =>
        {
            Received[topic]++;
            each?.Invoke(d);
        }, _ => { }));
    }

    public string Partition { get; }

    /// <summary>The random nonzero identity put on every player sample.</summary>
    public ulong ClientId { get; }

    public double Now => _clock();

    public Dictionary<int, DpsEntity> Entities { get; } = new();

    public D.Session? Session { get; private set; }

    public D.PlayerSlot? Slot { get; private set; }

    public D.OwnUnit? Own { get; private set; }

    /// <summary>The live gizmo layers by producer and layer name.</summary>
    public IReadOnlyDictionary<(string Producer, string Layer), D.GizmoFrame> Gizmos => _gizmos;

    public bool IsPlaying => Slot is { State: D.SlotState.SlotStatePlaying };

    private static ulong RandomId()
    {
        Span<byte> bytes = stackalloc byte[8];
        ulong id;
        do
        {
            RandomNumberGenerator.Fill(bytes);
            id = BitConverter.ToUInt64(bytes);
        }
        while (id == 0);
        return id;
    }

    /// <summary>Takes what the gateway published since the last call.</summary>
    public void Pump()
    {
        double now = _clock();
        Drain(_session, s =>
        {
            Session = s;
            if (s.WorldEpoch != WorldEpoch)
            {
                OnNewWorld(s.WorldEpoch);
            }
        }, _ => Session = null);
        Drain(_entity, e =>
        {
            Received[DpsProtocol.Entity]++;
            EntityFor(e.EntityId).Info = e;
        }, e => Entities.Remove(e.EntityId));
        Drain(_state, s =>
        {
            Received[DpsProtocol.EntityState]++;
            LatestSimTime = Math.Max(LatestSimTime, s.SimTime);
            var entity = EntityFor(s.EntityId);
            entity.State = s;
            entity.ReceivedAt = now;
        }, s => Entities.Remove(s.EntityId));
        Drain(_slot, s => Slot = s, _ => Slot = null);
        Drain(_own, o => Own = o, _ => Own = null);
        foreach (var pump in _countPumps)
        {
            pump();
        }

        Drain(_gizmo, g => _gizmos[(g.Producer, g.Layer)] = g, g => _gizmos.Remove((g.Producer, g.Layer)));
    }

    private DpsEntity EntityFor(int id)
    {
        if (!Entities.TryGetValue(id, out var entity))
        {
            Entities[id] = entity = new DpsEntity();
        }

        return entity;
    }

    private static void Drain<T>(DdsReader<T> reader, Action<T> alive, Action<T> gone)
        where T : struct
    {
        while (true)
        {
            int n = 0;
            using (var loan = reader.Take(256))
            {
                foreach (var sample in loan)
                {
                    n++;
                    var data = sample.Data;

                    // a not-alive sample (a disposed instance) means gone, never an error
                    if (sample.IsValid && sample.Info.InstanceState == DdsInstanceState.Alive)
                    {
                        alive(data);
                    }
                    else
                    {
                        gone(data);
                    }
                }
            }

            if (n == 0)
            {
                return;
            }
        }
    }

    /// <summary>Asks for the first free unit among the slot's unit offers (roles are not requested). False when there is none.</summary>
    public bool JoinFirstUnit()
    {
        HasLeft = false;
        if (Slot is not { } slot || slot.Offers is null)
        {
            return false;
        }

        foreach (var offer in slot.Offers)
        {
            if (offer.Kind == D.OfferKind.OfferKindUnit && offer.Free)
            {
                _join.Write(new D.JoinRequest { RequestSeq = ++_joinSeq, Kind = D.JoinKind.JoinKindUnit, UnitId = offer.Id, Role = string.Empty, ClientId = ClientId });
                return true;
            }
        }

        return false;
    }

    private double _lastAutoJoin = double.NegativeInfinity;

    /// <summary>Headless join: once a second while the slot is Choosing, never after the client has left.</summary>
    public bool TryAutoJoin()
    {
        double now = _clock();
        if (HasLeft || Slot is not { State: D.SlotState.SlotStateChoosing } || now - _lastAutoJoin < 1.0)
        {
            return false;
        }

        _lastAutoJoin = now;
        return JoinFirstUnit();
    }

    public void Leave()
    {
        HasLeft = true;
        _leave.Write(new D.Leave { Seq = ++_leaveSeq, ClientId = ClientId });
    }

    /// <summary>Body-relative movement, each -1..1 (forward, lateral right).</summary>
    public void SetMove(float forward, float lateral)
    {
        _frame.Forward = forward;
        _frame.Lateral = lateral;
    }

    public void SetAim(double x, double y, double z = 0.0)
    {
        _aimManual = true;
        _frame.AimPoint = new D.Vec3d { X = x, Y = y, Z = z };
    }

    public void SetTrigger(bool held)
    {
        _frame.Trigger = held ? (byte)1 : (byte)0;
    }

    /// <summary>A press: the counter changes (the gateway treats a change as one press).</summary>
    public void PressFire()
    {
        _frame.ShotSeq = unchecked((byte)(_frame.ShotSeq + 1));
    }

    /// <summary>Requests the next stance: standing, crouched, prone (sent as Stance + 1).</summary>
    public void CycleStance()
    {
        _stance = (_stance + 1) % 3;
        _frame.Stance = (byte)(_stance + 1);
    }

    private int _stance;

    private bool _aimManual;

    private const double EyeHeight = 1.6, SightRange = 100.0;

    /// <summary>
    /// Fills LookFrom (own position + 1.6 m up) and LookDir (the unit's horizontal facing, body +x) from the own unit's state, and, unless the caller aims by hand,
    /// AimPoint = LookFrom + 100 m along LookDir. Nothing changes while the own unit's state is unknown.
    /// </summary>
    public void UpdateLook()
    {
        if (Slot is not { UnitId: > 0 } slot || !Entities.TryGetValue(slot.UnitId, out var own) || own.State is not { } state)
        {
            return;
        }

        var q = state.Orientation;
        double yaw = Math.Atan2(2.0 * (q.W * q.Z + q.X * q.Y), 1.0 - 2.0 * (q.Y * q.Y + q.Z * q.Z));
        double dx = Math.Cos(yaw), dy = Math.Sin(yaw);
        var p = own.PositionAt(_clock());
        var from = new D.Vec3d { X = p.X, Y = p.Y, Z = p.Z + EyeHeight };
        _frame.LookFrom = from;
        _frame.LookDir = new D.Vec3f { X = (float)dx, Y = (float)dy, Z = 0f };
        if (!_aimManual)
        {
            _frame.AimPoint = new D.Vec3d { X = from.X + dx * SightRange, Y = from.Y + dy * SightRange, Z = from.Z };
        }
    }

    /// <summary>Writes the current control frame with the next sequence number.</summary>
    public void SendControl()
    {
        UpdateLook();
        _frame.Seq++;
        _frame.ClientId = ClientId;
        _control.Write(_frame);
    }

    /// <summary>Tells the gateway which gizmo layers and selected units the client wants (an empty list stops the projectors).</summary>
    public void SetGizmoView(IEnumerable<string> layers, IEnumerable<int> selected, float rateHz = 10f)
    {
        var view = new D.GizmoView
        {
            Layers = [.. layers.Select(l => new D.NameItem { Name = l })],
            SelectedIds = [.. selected],
            PinnedIds = [],
            PinnedFamilies = [],
            RateHz = rateHz,
            ClientId = ClientId,
        };
        _lastView = view;
        _view.Write(view);
    }

    public void Dispose()
    {
        _session.Dispose();
        _entity.Dispose();
        _state.Dispose();
        _slot.Dispose();
        _own.Dispose();
        _gizmo.Dispose();
        _join.Dispose();
        _control.Dispose();
        _leave.Dispose();
        _view.Dispose();
        foreach (var c in _counters)
        {
            c.Dispose();
        }

        _participant.Dispose();
    }
}
