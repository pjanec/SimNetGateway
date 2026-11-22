using SimNetGateway.Engine;
using SimNetGateway.Protocol.Hrot;
using D = SimNetGateway.Protocol.DdsPlayer;

namespace SimNetGateway.Plugin.DdsPlayer;

/// <summary>
/// Writes the voice events (DD-026) beside <see cref="DpsOutbound"/>: what a man shouts (<see cref="D.Utterance"/>), a report up or down the chain (<see cref="D.Report"/>) and an order given
/// (<see cref="D.Order"/>), each once on the world partition (the events of a world, counted in the render barrier's world events like a Fire), when the host sends one. The host decides what is new
/// (HROT's own state: a changed <c>Utterance</c>, a new history entry); this module only maps and writes. HROT's report numbers are the contract's enum numbers, kept when in range and none (0) when not.
/// One instance per gateway runtime.
/// </summary>
public sealed class DpsVoiceOutbound : IGatewayModule
{
    /// <summary>The bound of <see cref="D.Utterance.Kind"/>.</summary>
    public const int MaxKind = 24;

    /// <summary>The bound of <see cref="D.Order.Name"/>.</summary>
    public const int MaxOrder = 32;

    private readonly DpsPorts _ports;

    internal DpsVoiceOutbound(DpsPorts ports) => _ports = ports;

    public string Name => nameof(DpsVoiceOutbound);

    public void Register(ModuleSetup setup)
    {
        setup.OnMessage<HrotUtterance>(OnUtterance);
        setup.OnMessage<HrotReport>(OnReport);
        setup.OnMessage<HrotOrder>(OnOrder);
    }

    private void OnUtterance(in HrotUtterance m, IngressContext ctx)
    {
        string kind = m.Kind ?? string.Empty;
        _ports.Utterance.Send(new DpsItem<D.Utterance>(new D.Utterance
        {
            SimTime = m.SimTime,
            SpeakerId = m.SpeakerId,
            Kind = kind.Length > MaxKind ? kind[..MaxKind] : kind,
            TargetId = m.TargetId,
        }));
    }

    private void OnReport(in HrotReport m, IngressContext ctx) =>
        _ports.Report.Send(new DpsItem<D.Report>(new D.Report
        {
            SimTime = m.SimTime,
            FromId = m.FromId,
            ToId = m.ToId,
            Kind = m.Kind <= (byte)D.ReportKind.ReportKindRequest ? (D.ReportKind)m.Kind : D.ReportKind.ReportKindNone,
            Sub = m.Sub,
            Count = m.Count,
            Size = m.Size <= (byte)D.ElementSize.ElementSizePlatoon ? (D.ElementSize)m.Size : D.ElementSize.ElementSizeUnknown,
            Activity = m.Activity <= (byte)D.ElementActivity.ElementActivitySurrendering ? (D.ElementActivity)m.Activity : D.ElementActivity.ElementActivityUnknown,
            Equipment = m.Equipment,
            Strength = Colour(m.Strength),
            Ammo = Colour(m.Ammo),
            Wia = m.Wia,
            Kia = m.Kia,
            Synthetic = m.Synthetic,
        }));

    private void OnOrder(in HrotOrder m, IngressContext ctx)
    {
        string name = m.Name ?? string.Empty;
        _ports.Order.Send(new DpsItem<D.Order>(new D.Order
        {
            SimTime = m.SimTime,
            FromId = m.FromId,
            ToId = m.ToId,
            Name = name.Length > MaxOrder ? name[..MaxOrder] : name,
        }));
    }

    private static D.StatusColour Colour(byte value) => value <= (byte)D.StatusColour.StatusColourBlack ? (D.StatusColour)value : D.StatusColour.StatusColourUnknown;
}
