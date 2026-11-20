using SimNetGateway.Engine;
using SimNetGateway.Protocol.Hrot;
using D = SimNetGateway.Protocol.DdsPlayer;

namespace SimNetGateway.Plugin.DdsPlayer;

/// <summary>
/// Maps a building's damage (DD-018 3.6a) to its state topic, beside <see cref="DpsOutbound"/>: one instance per building, disposed when the host says the building has no
/// damage any more. The world boundary ends the rest with the transport's writers. Bounds: 64 breaches and 256 windows per building (HROT holds 16 and 64 per damage entity);
/// beyond them the later ones (in panel order) are left out. One instance per gateway runtime.
/// </summary>
public sealed class DpsBuildingOutbound : IGatewayModule
{
    /// <summary>The bounds of the contract's sequences, and of its key.</summary>
    public const int MaxBreaches = 64, MaxWindows = 256, MaxKey = 256;

    private readonly DpsPorts _ports;
    private readonly HashSet<string> _written = new(StringComparer.Ordinal);
    private readonly HashSet<string> _warned = new(StringComparer.Ordinal);
    private readonly Func<Action<string>> _log;

    internal DpsBuildingOutbound(DpsPorts ports, Func<Action<string>>? log = null)
    {
        _ports = ports;
        _log = log ?? (() => _ => { });
    }

    public string Name => nameof(DpsBuildingOutbound);

    public void Register(ModuleSetup setup) => setup.OnMessage<HrotBuildingDamage>(OnDamage);

    private void OnDamage(in HrotBuildingDamage m, IngressContext ctx)
    {
        string key = m.Building is { Length: > MaxKey } k ? k[..MaxKey] : m.Building ?? string.Empty;
        if (!m.Collapsed && m.Breaches.Count == 0 && m.Windows.Count == 0)
        {
            if (_written.Remove(key))
            {
                _ports.BuildingDamage.Send(new DpsItem<D.BuildingDamage>(new D.BuildingDamage { Building = key, Breaches = [], Windows = [] }, true));
            }

            return;
        }

        _written.Add(key);
        if ((m.Breaches.Count > MaxBreaches || m.Windows.Count > MaxWindows) && _warned.Add(key))
        {
            // one line per building, not per update: the damage beyond the bounds is not on the wire
            _log()($"dps: warning: building '{key}' has {m.Breaches.Count} breaches and {m.Windows.Count} windows; only the first {MaxBreaches} breaches and {MaxWindows} windows are sent");
        }

        _ports.BuildingDamage.Send(new DpsItem<D.BuildingDamage>(new D.BuildingDamage
        {
            Building = key,
            Collapsed = m.Collapsed,
            Breaches =
            [
                .. m.Breaches.Take(MaxBreaches).Select(b => new D.Breach
                {
                    Panel = b.Panel, From = b.From, To = b.To, Bottom = b.Bottom, Top = b.Top, Kind = LookNames.ByName<D.BreachKind>("BreachKind", b.Kind),
                }),
            ],
            Windows = [.. m.Windows.Take(MaxWindows).Select(w => new D.WindowDamage { Panel = w.Panel, Opening = w.Opening, Hits = w.Hits, U0 = w.U0, V0 = w.V0, U1 = w.U1, V1 = w.V1 })],
        }));
    }
}
