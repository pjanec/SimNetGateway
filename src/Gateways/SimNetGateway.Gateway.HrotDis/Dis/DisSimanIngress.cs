using DISnet;
using SimNetGateway.Engine;

namespace SimNetGateway.Gateway.HrotDis;

/// <summary>What the simulation-management ingress modules share: the filters and the addressing rule.</summary>
internal static class DisSimanSupport
{
    /// <summary>
    /// True when a simulation-management PDU applies to the gateway: the exercise matches, it does not come from the gateway itself
    /// (echo), and its receiving id names this site and application or "all" of either (IEEE 1278.1: a PDU addressed to another
    /// application is not for us). Counted as ignored otherwise.
    /// </summary>
    public static bool Applies(HrotDisServices services, byte exercise, EntityID originating, EntityID receiving)
    {
        var from = originating.SimulationAddress;
        if (DisIngressSupport.Reject(services, exercise, from.Site, from.Application))
        {
            return false;
        }

        var to = receiving.SimulationAddress;
        bool siteOk = to.Site == DisConversions.All || to.Site == services.OwnSite;
        bool applicationOk = to.Application == DisConversions.All || to.Application == services.OwnApplication;
        if (!siteOk || !applicationOk)
        {
            services.DisIngress.SimanIgnored++;
            return false;
        }

        return true;
    }
}

/// <summary>
/// Start/Resume PDU: when DIS controls HROT's time (<c>dis.disControlsHrot</c>, default off, R-087) and the engine session is
/// Paused, it becomes a Resume session command of source dis, which <c>TimeRequestEgress</c> turns into a <c>TimeRequest</c> for
/// HROT. Otherwise it is counted and ignored: the engine session only mirrors HROT, which stays the master of time. A PDU that is
/// not addressed to the gateway, or of the gateway itself, is ignored.
/// </summary>
public sealed class DisStartResumeIngress : IGatewayModule
{
    private readonly HrotDisServices _services;

    public DisStartResumeIngress(HrotDisServices services) => _services = services;

    public string Name => nameof(DisStartResumeIngress);

    public void Register(ModuleSetup setup) => setup.OnMessage<StartResumePdu>(Handle);

    private void Handle(in StartResumePdu m, IngressContext ctx)
    {
        if (!DisSimanSupport.Applies(_services, m.ExerciseID, m.OriginatingEntityID, m.ReceivingEntityID))
        {
            return;
        }

        if (_services.Config.Dis is { DisControlsHrot: true } && ctx.Session.State == SessionState.Paused)
        {
            ctx.Command(SessionCommand.Resume);
        }
        else
        {
            _services.DisIngress.SimanIgnored++;
        }
    }
}

/// <summary>Stop/Freeze PDU: as <see cref="DisStartResumeIngress"/>, a Pause command while the engine session is Running.</summary>
public sealed class DisStopFreezeIngress : IGatewayModule
{
    private readonly HrotDisServices _services;

    public DisStopFreezeIngress(HrotDisServices services) => _services = services;

    public string Name => nameof(DisStopFreezeIngress);

    public void Register(ModuleSetup setup) => setup.OnMessage<StopFreezePdu>(Handle);

    private void Handle(in StopFreezePdu m, IngressContext ctx)
    {
        if (!DisSimanSupport.Applies(_services, m.ExerciseID, m.OriginatingEntityID, m.ReceivingEntityID))
        {
            return;
        }

        if (_services.Config.Dis is { DisControlsHrot: true } && ctx.Session.State == SessionState.Running)
        {
            ctx.Command(SessionCommand.Pause);
        }
        else
        {
            _services.DisIngress.SimanIgnored++;
        }
    }
}

/// <summary>
/// Remove Entity PDU: destroys the entity of origin dis that the PDU's receiving id names (DD-006 7). An id that is unknown, or
/// names an entity the DIS side does not own in the pivot (one the gateway published for HROT: HROT decides when it goes away),
/// changes nothing and is counted. PDUs of another exercise and the gateway's own are ignored.
/// </summary>
public sealed class DisRemoveEntityIngress : IGatewayModule
{
    private readonly HrotDisMaps _maps;
    private readonly HrotDisSources _sources;
    private readonly HrotDisServices _services;

    public DisRemoveEntityIngress(HrotDisMaps maps, HrotDisSources sources, HrotDisServices services)
    {
        _maps = maps;
        _sources = sources;
        _services = services;
    }

    public string Name => nameof(DisRemoveEntityIngress);

    public void Register(ModuleSetup setup) => setup.OnMessage<RemoveEntityPdu>(Handle);

    private void Handle(in RemoveEntityPdu m, IngressContext ctx)
    {
        var from = m.OriginatingEntityID.SimulationAddress;
        if (DisIngressSupport.Reject(_services, m.ExerciseID, from.Site, from.Application))
        {
            return;
        }

        if (DisIngressSupport.TryResolve(ctx, _maps, m.ReceivingEntityID, out var entity) &&
            ctx.State.Find(entity) is { } record && record.Origin == _sources.Dis)
        {
            ctx.Changes.DestroyEntity(entity);
        }
        else
        {
            _services.DisIngress.RemovesIgnored++;
        }
    }
}
