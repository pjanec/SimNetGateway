using SimNetGateway.Engine;
using SimNetGateway.Protocol.Hrot;
using D = SimNetGateway.Protocol.DdsPlayer;

namespace SimNetGateway.Plugin.DdsPlayer;

/// <summary>
/// Writes <see cref="D.EntityStatus"/> (DD-025), beside <see cref="DpsOutbound"/>: what a client draws an entity's card from, one instance per entity the host says has a card, written once on the
/// world partition when the host sends a changed status (the host decides what changed; this module only maps and writes). The instance is disposed when the host withdraws it or the entity
/// goes. The world boundary ends the rest with the transport's writers. One instance per gateway runtime.
/// </summary>
public sealed class DpsStatusOutbound : IGatewayModule
{
    /// <summary>The bound of <see cref="D.EntityStatus.Symbol"/>.</summary>
    public const int MaxSymbol = 40;

    /// <summary>The bound of <see cref="D.EntityStatus.Behaviour"/> and <see cref="D.EntityStatus.Task"/>.</summary>
    public const int MaxName = 32;

    private readonly DpsPorts _ports;
    private readonly HashSet<int> _written = new();

    internal DpsStatusOutbound(DpsPorts ports) => _ports = ports;

    public string Name => nameof(DpsStatusOutbound);

    public void Register(ModuleSetup setup)
    {
        setup.OnMessage<HrotEntityStatus>(OnStatus);
        setup.OnMessage<HrotEntityStatusRemoved>(OnWithdrawn);
        setup.OnMessage<HrotEntityRemoved>(OnRemoved);
    }

    private void OnStatus(in HrotEntityStatus m, IngressContext ctx)
    {
        string symbol = m.Symbol ?? string.Empty;
        string behaviour = m.Behaviour ?? string.Empty;
        string task = m.Task ?? string.Empty;
        _written.Add(m.Id);
        _ports.EntityStatus.Send(new DpsItem<D.EntityStatus>(new D.EntityStatus
        {
            EntityId = m.Id,
            Symbol = symbol.Length > MaxSymbol ? symbol[..MaxSymbol] : symbol,
            HealthCurrent = Finite(m.HealthCurrent, 0f),
            HealthMax = Finite(m.HealthMax, 0f),
            StrengthPresent = m.StrengthPresent,
            StrengthAuthorized = m.StrengthAuthorized,
            AmmoShare = Finite(m.AmmoShare, -1f),
            MindFill = Finite(m.MindFill, 0f),
            MindBreak = Finite(m.MindBreak, -1f),
            Behaviour = behaviour.Length > MaxName ? behaviour[..MaxName] : behaviour,
            Task = task.Length > MaxName ? task[..MaxName] : task,
            MindBand = m.MindBand switch
            {
                HrotMindBand.Calm => D.MindBand.MindBandCalm,
                HrotMindBand.Nervous => D.MindBand.MindBandNervous,
                HrotMindBand.Broken => D.MindBand.MindBandBroken,
                HrotMindBand.Fear => D.MindBand.MindBandFear,
                _ => D.MindBand.MindBandNone,
            },
        }));
    }

    private void OnWithdrawn(in HrotEntityStatusRemoved m, IngressContext ctx) => Dispose(m.Id);

    private void OnRemoved(in HrotEntityRemoved m, IngressContext ctx) => Dispose(m.Id);

    private void Dispose(int id)
    {
        if (_written.Remove(id))
        {
            _ports.EntityStatus.Send(new DpsItem<D.EntityStatus>(new D.EntityStatus { EntityId = id, Symbol = string.Empty }, true));
        }
    }

    private static float Finite(float value, float none) => float.IsFinite(value) ? value : none;
}
