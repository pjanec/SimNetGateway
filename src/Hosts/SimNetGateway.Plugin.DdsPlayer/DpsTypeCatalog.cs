using System.Text;
using SimNetGateway.Engine;
using SimNetGateway.Protocol.Hrot;
using D = SimNetGateway.Protocol.DdsPlayer;

namespace SimNetGateway.Plugin.DdsPlayer;

/// <summary>
/// Writes the TKB type records (DD-018 3.2) and the animal states (3.5a), beside <see cref="DpsOutbound"/>. A type is written when the host sends it (once per runtime);
/// an animal's instance goes with its entity. The world boundary ends both with the transport's writers. One instance per gateway runtime.
/// </summary>
public sealed class DpsTypeCatalog : IGatewayModule
{
    /// <summary>The shape of <see cref="D.TkbType.Json"/>.</summary>
    public const string Schema = "hrot/1";

    private readonly DpsPorts _ports;
    private readonly HashSet<int> _animals = new();

    internal DpsTypeCatalog(DpsPorts ports) => _ports = ports;

    public string Name => nameof(DpsTypeCatalog);

    public void Register(ModuleSetup setup)
    {
        setup.OnMessage<HrotTypeRecord>(OnType);
        setup.OnMessage<HrotAnimal>(OnAnimal);
        setup.OnMessage<HrotEntityRemoved>(OnRemoved);
    }

    /// <summary>FNV-1a 64 of the UTF-8 bytes of <paramref name="json"/>.</summary>
    public static ulong HashOf(string json)
    {
        ulong hash = 14695981039346656037UL;
        foreach (byte b in Encoding.UTF8.GetBytes(json))
        {
            hash = (hash ^ b) * 1099511628211UL;
        }

        return hash;
    }

    private void OnType(in HrotTypeRecord m, IngressContext ctx)
    {
        string json = m.Json ?? string.Empty;
        _ports.TkbType.Send(new DpsItem<D.TkbType>(new D.TkbType
        {
            TypeId = m.TypeId,
            Name = Cut(m.Name, 64),
            CategoryPath = Cut(m.CategoryPath, 128),
            DisType = Cut(m.DisType, 64),
            Schema = Schema,
            ContentHash = HashOf(json),
            Json = json,
        }));
    }

    private void OnAnimal(in HrotAnimal m, IngressContext ctx)
    {
        _animals.Add(m.Id);
        _ports.Animal.Send(new DpsItem<D.Animal>(new D.Animal
        {
            EntityId = m.Id,
            Mode = LookNames.ByName<D.AnimalMode>("AnimalMode", m.Mode),
            FearBand = m.FearBand,
            Bolting = m.Bolting,
            Dangerous = m.Dangerous,
            Calling = m.Calling,
            FlockCount = m.FlockCount,
            FlockInitialCount = m.FlockInitialCount,
            FlockSeed = m.FlockSeed,
            FlockMode = LookNames.ByName<D.FlockMode>("FlockMode", m.FlockMode),
            Airborne = m.Airborne,
        }));
    }

    private void OnRemoved(in HrotEntityRemoved m, IngressContext ctx)
    {
        if (_animals.Remove(m.Id))
        {
            _ports.Animal.Send(new DpsItem<D.Animal>(new D.Animal { EntityId = m.Id }, true));
        }
    }

    private static string Cut(string? s, int max) => s is null ? string.Empty : s.Length <= max ? s : s[..max];
}
