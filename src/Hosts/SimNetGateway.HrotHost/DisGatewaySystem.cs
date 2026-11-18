using Fdp.Core;
using Fdp.ModuleHost.Abstractions;
using Fdp.Toolkit.Orchestration;
using Fdp.Toolkit.Replay;
using Fdp.Toolkit.Replication.Services;
using Fdp.Toolkit.Time;
using SimNetGateway.Protocol.Hrot;

namespace SimNetGateway.HrotHost;

/// <summary>
/// The gateway's one system: in the Input phase, every frame on the main thread -
/// gather (HROT entities and last frame's events to messages), tick (the gateway engine), apply (replicas and events), pose (replicas, only
/// while the clock advances). Gated like ingress while the node restores a recording (<see cref="IReplayRestoreGated"/>); resets everything it
/// holds at a world boundary (<see cref="WorldEpoch.Moved"/>).
/// </summary>
[UpdateInPhase(SystemPhase.Input)]
[TickMode(TickPolicy.EveryFrame)]
public sealed class DisGatewaySystem : IEcsModuleSystem, Fdp.Interfaces.IReplayRestoreGated, IDisposable
{
    private readonly HrotServices _services;
    private readonly Func<GatewayRuntime> _factory;
    private readonly EcsHrotAdapter _adapter;
    private readonly ReplicaPoser _poser;
    private readonly bool _allowZeroOrigin;
    private readonly double _gatherPeriod;
    private readonly Action<string> _error;
    private readonly Action? _beforeFrame;

    private GatewayRuntime? _runtime;
    private EntityRepository? _world;
    private int _epoch;
    private bool _restored;
    private bool _wasReplaying;
    private bool _failed;
    private bool _disposed;
    private ClusterPhase? _failedPhase;
    private HrotGeoRef? _sentGeoRef;
    private HrotOrigin? _origin;
    private int _originGeneration;
    private ClusterPhase? _sentPhase;
    private bool _sentAdvancing;
    private double _lastGather = double.NegativeInfinity;

    public DisGatewaySystem(HrotServices services, Gateway.HrotDis.GatewayConfiguration config, Func<GatewayRuntime> factory, Action<string>? error = null, Action? beforeFrame = null)
    {
        _services = services ?? throw new ArgumentNullException(nameof(services));
        _factory = factory ?? throw new ArgumentNullException(nameof(factory));
        _adapter = new EcsHrotAdapter(services, config);
        _allowZeroOrigin = config.Hrot.AllowZeroOrigin;
        _poser = new ReplicaPoser(_allowZeroOrigin);
        _gatherPeriod = 1.0 / Math.Max(0.1, config.Hrot.SampleRateHz);
        _error = error ?? (line => Console.Error.WriteLine("[SimNetGateway] " + line));
        _beforeFrame = beforeFrame;
    }

    /// <inheritdoc />
    public Func<bool>? IsReplayRestoring { get; set; }

    /// <summary>The adapter (the replica book and the counters).</summary>
    public EcsHrotAdapter Adapter => _adapter;

    /// <summary>The running gateway; null while idle or after a failed start.</summary>
    public GatewayRuntime? Runtime => _runtime;

    /// <summary>How many times the gateway engine was built (a world boundary rebuilds it).</summary>
    public int Builds { get; private set; }

    /// <summary>How many frames the system ran (diagnostics).</summary>
    public long Executions { get; private set; }

    /// <summary>The geo reference last sent to the gateway (null until one was).</summary>
    public HrotOrigin? LastOrigin => _origin;

    /// <summary>The generation of <see cref="LastOrigin"/>: grows with every change of any field of the geo reference.</summary>
    public int OriginGeneration => _originGeneration;

    /// <summary>Replicas written by the poser in the last frame.</summary>
    public int LastPosed { get; private set; }

    public void Execute(ISimulationView view, float deltaTime)
    {
        if (view is not EntityRepository world)
        {
            throw new InvalidOperationException($"{nameof(DisGatewaySystem)} needs the live EntityRepository, not {view.GetType().Name}.");
        }

        if (_disposed)
        {
            return;   // the node is ending: nothing is started again
        }

        Executions++;

        // the plugins' host services belong to the capability and are serviced every frame, also while no gateway runs (Idle) or a recording is restored
        _beforeFrame?.Invoke();

        // While this node restores its own recording (the controller wires this gate for the WHOLE replay of an own recording, not for moments of it) the recording owns the world:
        // nothing enters it (like any replay: no replica ingress, no poser) but the gateway keeps showing what the recording holds - HROT is the source of the other protocols.
        bool restoring = IsReplayRestoring?.Invoke() == true;

        if (!ReferenceEquals(world, _world))
        {
            bool replaced = _world is not null;
            _world = world;
            EcsHrotAdapter.EnsureRegistered(world);
            _epoch = 0;
            WorldEpoch.Moved(world, ref _epoch);   // takes the world's current epoch as the baseline
            if (replaced)
            {
                Boundary(world);
            }
        }
        else if (WorldEpoch.Moved(world, ref _epoch) | (_restored && !restoring))
        {
            Boundary(world);   // the world moved, or the restore ended and the maps hold ids of the recording's world
        }

        _restored = restoring;

        var state = _services.State();
        var phase = PhaseOf(state);
        bool replaying = ClusterStates.IsReplaying(state) || ReplayState.IsActive(world) || restoring;
        if (replaying && !_wasReplaying)
        {
            _adapter.RemoveAllReplicas(world);   // replicas are NoReplay: the recording shows only what it recorded
        }

        _wasReplaying = replaying;

        if (_runtime is null)
        {
            if (phase == ClusterPhase.Idle || (_failed && _failedPhase == phase))
            {
                return;
            }

            if (!Start(phase))
            {
                return;
            }
        }

        var runtime = _runtime!;
        var clock = SimClock.Of(world);
        PublishFrame(runtime, phase, clock, _services.GeoReference());
        double now = runtime.Clock.Now;
        if (phase != ClusterPhase.Idle)
        {
            if (now - _lastGather >= _gatherPeriod)
            {
                _lastGather = now;
                _adapter.GatherEntities(world, runtime.HrotIn, now, clock.TotalTime);
                _adapter.GatherTurrets(world, runtime.HrotIn, now);
            }

            // the gateway gathers in a replay when the shared flag says so or a running plugin wants events then; every side keeps its own gate on what it sends
            if (!replaying || runtime.EventsInReplay)
            {
                _adapter.GatherEvents(world, runtime.HrotIn);
            }

            // a replay does not play events back: fire and detonations come from what the recorded world remembers
            if (replaying)
            {
                if (runtime.EventsInReplay)
                {
                    _adapter.GatherReplayEffects(world, runtime.HrotIn, clock.TotalTime);
                }
            }
            else
            {
                _adapter.ForgetReplayEffects();
            }
        }

        runtime.Tick();
        _adapter.Apply(world, runtime.HrotOut, ingress: !replaying);

        LastPosed = 0;
        if (clock.IsAdvancing && !replaying && phase != ClusterPhase.Idle)
        {
            LastPosed = _poser.Pose(world, _adapter.Replicas.Values, _origin, now);
        }
    }

    private void PublishFrame(GatewayRuntime runtime, ClusterPhase phase, ISimClock clock, HrotGeoRef? geoRef)
    {
        // (0, 0, 0) with a frame is the pre-frame "not set" of a bare transform: not sent unless allowed
        bool notSet = geoRef is { HasFrame: true, LatDeg: 0.0, LonDeg: 0.0, AltM: 0.0 } && !_allowZeroOrigin;
        if (geoRef is { } g && !notSet && geoRef != _sentGeoRef)
        {
            _sentGeoRef = g;
            _originGeneration++;
            _origin = g.HasFrame
                ? HrotOrigin.Utm(g.LatDeg, g.LonDeg, g.AltM, _originGeneration, g.ZoneOverride, g.SouthOverride)
                : HrotOrigin.None(_originGeneration);
            runtime.HrotIn.Enqueue(_origin.Value);
        }

        bool advancing = clock.IsAdvancing;
        if (_sentPhase != phase || _sentAdvancing != advancing)
        {
            _sentPhase = phase;
            _sentAdvancing = advancing;
            runtime.HrotIn.Enqueue(new HrotSession(phase, advancing, clock.TimeScale, clock.TotalTime));
        }
    }

    private bool Start(ClusterPhase phase)
    {
        try
        {
            _runtime = _factory();
            Builds++;
            _failed = false;
            _failedPhase = null;
            _sentGeoRef = null;
            _origin = null;
            _sentPhase = null;
            _lastGather = double.NegativeInfinity;
            return true;
        }
        catch (Exception ex)
        {
            // a port in use or a socket error: reported once, idle until the phase changes (the editor keeps running)
            _failed = true;
            _failedPhase = phase;
            _error($"the gateway could not start: {ex.GetType().Name}: {ex.Message}");
            return false;
        }
    }

    /// <summary>The world was put back: forget the books, sweep restored replicas, rebuild the engine (its maps hold ids of the old world).</summary>
    private void Boundary(EntityRepository world)
    {
        _adapter.Reset(world);
        StopRuntime(farewell: false);
        _failed = false;
        _failedPhase = null;
        _wasReplaying = false;
    }

    private void StopRuntime(bool farewell)
    {
        var runtime = _runtime;
        _runtime = null;
        if (runtime is null)
        {
            return;
        }

        try
        {
            if (farewell)
            {
                runtime.HrotIn.Enqueue(new HrotSession(ClusterPhase.Idle, false, 1.0, 0.0));   // final Deactivated states
                runtime.Tick();
            }
        }
        catch (Exception ex)
        {
            _error($"the final tick failed: {ex.Message}");
        }
        finally
        {
            runtime.Dispose();
        }
    }

    /// <summary>The session phase of a cluster state (loading, operating and unloading of one family are the same phase).</summary>
    public static ClusterPhase PhaseOf(ClusterState state) => state switch
    {
        ClusterState.Idle => ClusterPhase.Idle,
        ClusterState.LoadingEdit or ClusterState.OperatingEdit or ClusterState.UnloadingEdit => ClusterPhase.Edit,
        ClusterState.LoadingPreview or ClusterState.OperatingPreview or ClusterState.UnloadingPreview => ClusterPhase.Preview,
        ClusterState.LoadingLive or ClusterState.OperatingLive or ClusterState.UnloadingLive => ClusterPhase.Live,
        ClusterState.LoadingReplay or ClusterState.OperatingReplay or ClusterState.UnloadingReplay => ClusterPhase.Replay,
        ClusterState.LoadingReview or ClusterState.OperatingReview or ClusterState.UnloadingReview => ClusterPhase.Review,
        _ => ClusterPhase.Degraded,
    };

    /// <summary>
    /// The node's teardown (HROT disposes the extension's capability, which disposes this system, BEFORE the world and the context): the final Deactivated states (a closing tick of the engine; a plugin's transport may dispose what it wrote), the DIS socket and its receive thread (bounded join) are closed. The world is not touched (it dies with the process; replicas are swept
    /// at a world boundary, on the simulation thread). Idempotent; a system whose gateway never started does nothing; a failure in one part is reported and does not stop the next.
    /// </summary>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        // the process-teardown path never reads or writes the world: it dies with the process, and on a threaded node this runs on the main thread after the simulation
        // thread stopped (the world guard flags such a read). Replicas are swept where the system runs on the simulation thread: at a world boundary and when a replay begins.
        StopRuntime(farewell: true);
    }
}
