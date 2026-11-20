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
        _frame.GaitMode = D.Gait.GaitRun;   // the new enumeration and the old byte (0 = run) both say run
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
        SubscribeRoundTwo();
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
        foreach (var clear in _clears)
        {
            clear();
        }

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


    // ---- DPS round 2: the state the client keeps besides the entities ----------------------------------------------------------

    /// <summary>The kill feed keeps this many lines.</summary>
    public const int KillFeedLines = 5;

    private readonly List<Action> _clears = new();
    private readonly List<string> _killFeed = new();
    private readonly List<(D.PlayerMessage Message, double At)> _messages = new();
    private readonly HashSet<(int Id, int Origin)> _messagesSeen = new();
    private int? _lastUsePress;
    private string _lastUseLabel = string.Empty;

    /// <summary>The rounds with a look, by the round's entity id.</summary>
    /// <summary>Where each person aims and looks (intent), by person id; see <see cref="AimPointOf"/>.</summary>
    public Dictionary<int, D.PersonAim> Aims { get; } = new();

    public Dictionary<int, D.Round> Rounds { get; } = new();

    public Dictionary<long, D.MunitionLook> MunitionLooks { get; } = new();

    /// <summary>The lit flame jets by (entity id, mount).</summary>
    public Dictionary<(int Entity, int Mount), D.FlameJet> FlameJets { get; } = new();

    public Dictionary<int, D.FireSpots> FireSpots { get; } = new();

    public Dictionary<int, D.GroundMarks> GroundMarks { get; } = new();

    public Dictionary<int, D.Animal> Animals { get; } = new();

    /// <summary>The TKB type records by type id.</summary>
    public Dictionary<long, D.TkbType> TkbTypes { get; } = new();

    /// <summary>The damage of the buildings by their terrain object label.</summary>
    public Dictionary<string, D.BuildingDamage> BuildingDamage { get; } = new(StringComparer.Ordinal);

    /// <summary>The rule statuses by id.</summary>
    public Dictionary<int, D.Status> Statuses { get; } = new();

    public D.Exercise? Exercise { get; private set; }

    public Dictionary<int, D.TrafficLights> TrafficLights { get; } = new();

    /// <summary>The ambient movers, played delayed (<see cref="MoverPlayback.PlaybackDelay"/>).</summary>
    public MoverPlayback Movers { get; } = new();

    /// <summary>The last <see cref="KillFeedLines"/> kill feed lines, oldest first.</summary>
    public IReadOnlyList<string> KillFeed => _killFeed;

    /// <summary>The player's station id, when the client knows it (messages to a station are shown only then).</summary>
    public int StationId { get; set; } = -1;

    /// <summary>Who looks, for the message and objective filters.</summary>
    public DpsViewer Viewer => new(Slot?.UnitId ?? 0, Slot?.Side ?? 0, StationId);

    private void Watch<T>(string topic, Action<T> alive, Action<T> gone)
        where T : struct
    {
        Received[topic] = 0;
        var reader = new DdsReader<T>(_participant);
        _counters.Add(reader);
        _countPumps.Add(() => Drain(reader, d =>
        {
            Received[topic]++;
            alive(d);
        }, gone));
    }

    private void Keep<T, TKey>(string topic, Dictionary<TKey, T> cache, Func<T, TKey> key)
        where T : struct
        where TKey : notnull
    {
        _clears.Add(cache.Clear);
        Watch<T>(topic, d => cache[key(d)] = d, d => cache.Remove(key(d)));
    }

    private void SubscribeRoundTwo()
    {
        Keep(DpsProtocol.PersonAim, Aims, (D.PersonAim a) => a.EntityId);
        Keep(DpsProtocol.Round, Rounds, (D.Round r) => r.EntityId);
        Keep(DpsProtocol.MunitionLook, MunitionLooks, (D.MunitionLook l) => l.MunitionTypeId);
        Keep(DpsProtocol.FlameJet, FlameJets, (D.FlameJet j) => (j.EntityId, j.Mount));
        Keep(DpsProtocol.FireSpots, FireSpots, (D.FireSpots s) => s.EntityId);
        Keep(DpsProtocol.GroundMarks, GroundMarks, (D.GroundMarks m) => m.EntityId);
        Keep(DpsProtocol.Animal, Animals, (D.Animal a) => a.EntityId);
        Keep(DpsProtocol.TkbType, TkbTypes, (D.TkbType t) => t.TypeId);
        Keep(DpsProtocol.BuildingDamage, BuildingDamage, (D.BuildingDamage b) => b.Building ?? string.Empty);
        Keep(DpsProtocol.Status, Statuses, (D.Status s) => s.StatusId);
        Keep(DpsProtocol.TrafficLights, TrafficLights, (D.TrafficLights l) => l.SignalId);
        _clears.Add(() => Exercise = null);
        Watch<D.Exercise>(DpsProtocol.Exercise, e => Exercise = e, _ => Exercise = null);

        // movers: who (state), where (batches of knots)
        _clears.Add(Movers.Clear);
        Watch<D.Mover>(DpsProtocol.Mover, m => Movers.SetMover(m), m => Movers.Remove(m.MoverId));
        Watch<D.MoverTracks>(DpsProtocol.MoverTracks, b => Movers.AddBatch(b, _clock()), _ => { });

        // events: the kill feed and the messages
        _clears.Add(_killFeed.Clear);
        _clears.Add(_messages.Clear);
        _clears.Add(_messagesSeen.Clear);
        Watch<D.ManDowned>(DpsProtocol.ManDowned, k =>
        {
            _killFeed.Add(DpsHud.KillLine(k, id => Entities.TryGetValue(id, out var e) && e.Info is { } info && !string.IsNullOrEmpty(info.Name) ? info.Name : "#" + id));
            while (_killFeed.Count > KillFeedLines)
            {
                _killFeed.RemoveAt(0);
            }
        }, _ => { });
        Watch<D.PlayerMessage>(DpsProtocol.PlayerMessage, m =>
        {
            // a message is kept once (the same id and origin is a repeat); who it is for is judged when it is shown, by whoever the player is then
            if (_messagesSeen.Add((m.MessageId, m.OriginNode)))
            {
                _messages.Add((m, _clock()));
                if (_messages.Count > 20)
                {
                    _messages.RemoveAt(0);
                }
            }
        }, _ => { });
    }

    /// <summary>The newest message addressed to the player that is still within its display time at client time <paramref name="now"/>.</summary>
    public D.PlayerMessage? CurrentMessage(double now)
    {
        for (int i = _messages.Count - 1; i >= 0; i--)
        {
            var (message, at) = _messages[i];
            if (now - at <= Math.Max(message.Seconds, 1f) && DpsHud.Addressed(message, Viewer))
            {
                return message;
            }
        }

        return null;
    }

    /// <summary>The objectives the player's side sees (title and state), as HROT's player HUD filters them.</summary>
    public IEnumerable<D.Status> VisibleObjectives()
    {
        var parents = new HashSet<int>(Statuses.Values.Select(s => s.ParentId).Where(p => p != 0));
        byte side = Slot?.Side ?? 0;
        return Statuses.Values.Where(s => DpsHud.VisibleTo(s, side, parents.Contains(s.StatusId))).OrderBy(s => s.StatusId);
    }

    /// <summary>The weapon line of the own unit, or null.</summary>
    public string? WeaponLine() => Own is { } own ? DpsHud.WeaponLine(own) : null;

    /// <summary>The use prompt of the own unit, or null.</summary>
    public string? UsePrompt() => Own is { } own ? DpsHud.UsePrompt(own) : null;


    /// <summary>
    /// The point a person aims at, at client time <paramref name="now"/> (DD-020 3.2): with an aim target the target's chest (its dead-reckoned position 1 m up, the
    /// producer's rule), else the aim point of the sample, which is also the fallback when the target is not known to this client. Null when the person is not aiming.
    /// </summary>
    public D.Vec3d? AimPointOf(int personId, double now)
    {
        if (!Aims.TryGetValue(personId, out var aim))
        {
            return null;
        }

        if (aim.AimTargetId != 0 && Entities.TryGetValue(aim.AimTargetId, out var target) && target.State is not null)
        {
            var p = target.PositionAt(now);
            return new D.Vec3d { X = p.X, Y = p.Y, Z = p.Z + ChestHeight };
        }

        return aim.AimPoint.X == 0 && aim.AimPoint.Y == 0 && aim.AimPoint.Z == 0 ? null : aim.AimPoint;
    }

    /// <summary>The point a person looks at (an entity's position, else the sample's point); null when his head is straight on the body.</summary>
    public D.Vec3d? LookPointOf(int personId, double now)
    {
        if (!Aims.TryGetValue(personId, out var aim) || !aim.HasLookTarget)
        {
            return null;
        }

        return aim.LookTargetId != 0 && Entities.TryGetValue(aim.LookTargetId, out var target) && target.State is not null ? target.PositionAt(now) : aim.LookTarget;
    }

    /// <summary>The chest above an entity's position in metres (the producer's aim rule, stated in the client guide).</summary>
    public const double ChestHeight = 1.0;
    /// <summary>The verdict of the client's last use press, once the unit's answer is for it.</summary>
    public string? UseVerdict() => Own is { } own ? DpsHud.UseVerdict(own, _lastUsePress, _lastUseLabel) : null;

    /// <summary>A use press on what the unit can use now: the press counter changes, the verb and target are the unit's own offer.</summary>
    public void PressUse()
    {
        if (Own is not { } own)
        {
            return;
        }

        _frame.UseSeq = unchecked((byte)(_frame.UseSeq + 1));
        _frame.UseVerb = own.UseVerb;
        _frame.Verb = own.Verb;
        _frame.UseTarget = own.UseTargetId;
        _lastUsePress = _frame.UseSeq;
        _lastUseLabel = own.UseLabel ?? string.Empty;
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
        _frame.StanceWanted = (D.StanceRequest)(_stance + 1);
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
