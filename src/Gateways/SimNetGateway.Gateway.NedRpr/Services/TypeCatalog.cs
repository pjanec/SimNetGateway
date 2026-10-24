using SimNetGateway.Protocol.Rpr;

namespace SimNetGateway.Gateway.NedRpr;

/// <summary>
/// The hardcoded (PoC) catalogue of entity types: TKB id &lt;-&gt; DIS entity type. An unknown value is not an error:
/// the caller uses a default (TKB 0 / <see cref="DefaultDis"/>) and logs a warning once per unknown value, which
/// <see cref="NoteUnknownTkb"/> and <see cref="NoteUnknownDis"/> decide.
/// </summary>
public sealed class TypeCatalog
{
    /// <summary>What a caller uses for an unknown TKB id.</summary>
    public static readonly DisEntityType DefaultDis = default;

    /// <summary>What a caller uses for an unknown DIS type.</summary>
    public const int DefaultTkb = 0;

    private static readonly (int Tkb, DisEntityType Dis)[] Entries =
    [
        (100, new DisEntityType(1, 1, 225, 1, 1, 3, 0)),  // main battle tank
        (101, new DisEntityType(1, 1, 225, 6, 1, 0, 0)),  // truck
        (102, new DisEntityType(1, 1, 225, 2, 1, 0, 0)),  // APC
        (200, new DisEntityType(1, 2, 225, 50, 1, 0, 0)), // UAV
        (300, new DisEntityType(1, 1, 225, 9, 0, 0, 0)),  // checkpoint / structure
        (400, new DisEntityType(1, 1, 222, 1, 2, 1, 0)),  // enemy tank
        (501, new DisEntityType(2, 9, 225, 2, 1, 1, 0)),  // munition
        (502, new DisEntityType(2, 9, 225, 2, 14, 1, 0)), // munition
    ];

    private readonly Dictionary<int, DisEntityType> _toDis = new();
    private readonly Dictionary<DisEntityType, int> _toTkb = new();
    private readonly HashSet<int> _warnedTkb = new();
    private readonly HashSet<DisEntityType> _warnedDis = new();

    public TypeCatalog()
    {
        foreach (var (tkb, dis) in Entries)
        {
            _toDis.Add(tkb, dis);
            _toTkb.Add(dis, tkb);
        }
    }

    /// <summary>All catalogued TKB ids.</summary>
    public IEnumerable<int> TkbTypes => _toDis.Keys;

    /// <summary>All catalogued DIS types.</summary>
    public IEnumerable<DisEntityType> DisTypes => _toTkb.Keys;

    public bool TryToDis(int tkb, out DisEntityType dis) => _toDis.TryGetValue(tkb, out dis);

    public bool TryToTkb(DisEntityType dis, out int tkb) => _toTkb.TryGetValue(dis, out tkb);

    /// <summary>True the first time this unknown TKB id is noted (the caller warns), false afterwards.</summary>
    public bool NoteUnknownTkb(int tkb) => _warnedTkb.Add(tkb);

    /// <summary>True the first time this unknown DIS type is noted (the caller warns), false afterwards.</summary>
    public bool NoteUnknownDis(DisEntityType dis) => _warnedDis.Add(dis);
}
