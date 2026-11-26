using SimNetGateway.Engine;
using SimNetGateway.Protocol.Hrot;
using D = SimNetGateway.Protocol.DdsPlayer;

namespace SimNetGateway.Plugin.DdsPlayer;

/// <summary>
/// Maps a laid bridge's deck (DD-035) to its state topic: one <c>Deck</c> instance per standing bridge, written when the host sends it (it stands, or its pose or shape changed) and disposed when the host says it is gone
/// (recovered, or fallen: the entity's own <c>EntityState.Damage</c> tells a fall). The bridge stays an <c>Entity</c> too. One instance per gateway runtime.
/// </summary>
public sealed class DpsDeckOutbound : IGatewayModule
{
    private readonly DpsPorts _ports;
    private readonly HashSet<int> _written = new();

    internal DpsDeckOutbound(DpsPorts ports) => _ports = ports;

    public string Name => nameof(DpsDeckOutbound);

    public void Register(ModuleSetup setup)
    {
        setup.OnMessage<HrotDeck>(OnDeck);
        setup.OnMessage<HrotDeckEnded>(OnEnded);
    }

    private void OnDeck(in HrotDeck m, IngressContext ctx)
    {
        _written.Add(m.Id);
        var parts = new List<D.DeckPart>(m.Parts.Count);
        for (int i = 0; i < m.Parts.Count; i++)
        {
            var p = m.Parts[i];
            parts.Add(new D.DeckPart { Kind = (D.DeckPartKind)(byte)p.Kind, C0 = V(p.C0), C1 = V(p.C1), C2 = V(p.C2), C3 = V(p.C3) });   // the pivot enum mirrors the contract's in order (a rail pins it)
        }

        _ports.Deck.Send(new DpsItem<D.Deck>(new D.Deck
        {
            EntityId = m.Id,
            Kind = (D.DeckKind)(byte)m.Kind,
            Length = m.Length,
            Width = m.Width,
            Height = m.Height,
            Parts = parts,
        }));
    }

    private void OnEnded(in HrotDeckEnded m, IngressContext ctx)
    {
        if (_written.Remove(m.Id))
        {
            _ports.Deck.Send(new DpsItem<D.Deck>(new D.Deck { EntityId = m.Id, Parts = [] }, true));
        }
    }

    private static D.Vec3d V(HrotVec3 v) => new() { X = v.X, Y = v.Y, Z = v.Z };
}
