using SimNetGateway.Engine;
using SimNetGateway.Protocol.Hrot;
using D = SimNetGateway.Protocol.DdsPlayer;

namespace SimNetGateway.Plugin.DdsPlayer;

/// <summary>
/// Maps the craters of a ground cell (DD-030) to its state topic, beside <see cref="DpsBuildingOutbound"/>: one instance per HROT cell, written once on the world partition, disposed when the host
/// says the cell has no craters any more. The world boundary ends the rest with the transport's writers. Bounds: 16 craters per cell (the most one HROT damage entity holds; a cell several
/// entities hold past 16 loses its later ones, warned about once per cell) and a 32-character key. One instance per gateway runtime.
/// </summary>
public sealed class DpsGroundOutbound : IGatewayModule
{
    /// <summary>The bounds of the contract's sequence and of its key.</summary>
    public const int MaxCraters = 16, MaxKey = 32;

    private readonly DpsPorts _ports;
    private readonly HashSet<string> _written = new(StringComparer.Ordinal);
    private readonly HashSet<string> _warned = new(StringComparer.Ordinal);
    private readonly Func<Action<string>> _log;

    internal DpsGroundOutbound(DpsPorts ports, Func<Action<string>>? log = null)
    {
        _ports = ports;
        _log = log ?? (() => _ => { });
    }

    public string Name => nameof(DpsGroundOutbound);

    public void Register(ModuleSetup setup) => setup.OnMessage<HrotGroundDamage>(OnGround);

    private void OnGround(in HrotGroundDamage m, IngressContext ctx)
    {
        string key = m.Cell is { Length: > MaxKey } k ? k[..MaxKey] : m.Cell ?? string.Empty;
        if (m.Craters.Count == 0)
        {
            if (_written.Remove(key))
            {
                _ports.GroundDamage.Send(new DpsItem<D.GroundDamage>(new D.GroundDamage { Cell = key, Craters = [] }, true));
            }

            return;
        }

        _written.Add(key);
        if (m.Craters.Count > MaxCraters && _warned.Add(key))
        {
            // one line per cell, not per update: the craters beyond the bound are not on the wire
            _log()($"dps: warning: ground cell '{key}' has {m.Craters.Count} craters; only the first {MaxCraters} are sent");
        }

        _ports.GroundDamage.Send(new DpsItem<D.GroundDamage>(new D.GroundDamage
        {
            Cell = key,
            Craters =
            [
                .. m.Craters.Take(MaxCraters).Select(c => new D.Crater
                {
                    Centre = new D.Vec3d { X = c.Centre.X, Y = c.Centre.Y, Z = c.Centre.Z },
                    Radius = c.Radius, Depth = c.Depth, Rim = c.Rim, BornAt = c.BornAt, Kind = LookNames.ByName<D.CraterKind>("CraterKind", c.Kind),
                }),
            ],
        }));
    }
}
