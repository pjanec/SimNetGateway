namespace SimNetGateway.Gateway.HrotDis;

/// <summary>
/// A DIS entity type (IEEE 1278.1 EntityType) and its packing into 64 bits as the pivot and <c>Protocol.Hrot</c> carry it:
/// kind 8, domain 8, country 16, category 8, subcategory 8, specific 8, extra 8 bits, the kind in the top byte (so the
/// packed value of a kind above 127 is negative). 0 means "no type".
/// </summary>
public readonly record struct DisTypeCode(byte Kind, byte Domain, ushort Country, byte Category, byte Subcategory, byte Specific, byte Extra)
{
    /// <summary>Kind, domain, country and category are all set (the HROT-provided type is then used as it is, R-088).</summary>
    public bool IsComplete => Kind != 0 && Domain != 0 && Country != 0 && Category != 0;

    public long Pack() => unchecked((long)(
        ((ulong)Kind << 56) | ((ulong)Domain << 48) | ((ulong)Country << 32) |
        ((ulong)Category << 24) | ((ulong)Subcategory << 16) | ((ulong)Specific << 8) | Extra));

    public static DisTypeCode Unpack(long packed)
    {
        ulong v = unchecked((ulong)packed);
        return new DisTypeCode((byte)(v >> 56), (byte)(v >> 48), (ushort)(v >> 32), (byte)(v >> 24), (byte)(v >> 16), (byte)(v >> 8), (byte)v);
    }

    /// <summary>True when the packed value names a complete type.</summary>
    public static bool IsCompletePacked(long packed) => Unpack(packed).IsComplete;

    /// <summary>
    /// How many leading fields (kind, domain, country, category, subcategory, specific, extra) two types share, 0 .. 7; a
    /// catalogue match needs at least 4 (down to the category).
    /// </summary>
    public static int CommonPrefix(DisTypeCode a, DisTypeCode b)
    {
        if (a.Kind != b.Kind) return 0;
        if (a.Domain != b.Domain) return 1;
        if (a.Country != b.Country) return 2;
        if (a.Category != b.Category) return 3;
        if (a.Subcategory != b.Subcategory) return 4;
        if (a.Specific != b.Specific) return 5;
        return a.Extra != b.Extra ? 6 : 7;
    }
}

/// <summary>
/// The catalogue of entity types in code (R-088): TKB type &lt;-&gt; packed DIS entity type. The type the HROT pack provides
/// comes first (<see cref="TryResolveDisType"/>); the table is the fallback and the only way from DIS to TKB. An unknown value
/// is not an error: the caller uses <see cref="DefaultTkb"/> (DIS to TKB) or leaves the type out (TKB to DIS) and warns once
/// per value, which <see cref="NoteUnknownTkb"/> and <see cref="NoteUnknownDis"/> decide. The entries are a PoC table; the
/// real HROT TKB numbering is supplied by the pack through <c>DisType</c>.
/// </summary>
public sealed class EntityTypeCatalog
{
    private static readonly (long Tkb, DisTypeCode Dis)[] Entries =
    [
        (100, new DisTypeCode(1, 1, 225, 1, 1, 3, 0)),  // main battle tank
        (101, new DisTypeCode(1, 1, 225, 6, 1, 0, 0)),  // truck
        (102, new DisTypeCode(1, 1, 225, 2, 1, 0, 0)),  // APC
        (200, new DisTypeCode(1, 2, 225, 50, 1, 0, 0)), // UAV
        (300, new DisTypeCode(1, 1, 225, 9, 0, 0, 0)),  // checkpoint / structure
        (400, new DisTypeCode(1, 1, 222, 1, 2, 1, 0)),  // enemy tank
    ];

    private readonly Dictionary<long, long> _toDis = new();
    private readonly List<(long Tkb, DisTypeCode Dis)> _all = new();
    private readonly HashSet<long> _warnedTkb = new();
    private readonly HashSet<long> _warnedDis = new();

    public EntityTypeCatalog(long defaultTkbForUnknown = 0)
    {
        DefaultTkb = defaultTkbForUnknown;
        foreach (var (tkb, dis) in Entries)
        {
            _toDis.Add(tkb, dis.Pack());
            _all.Add((tkb, dis));
        }
    }

    /// <summary>What a replica gets when its DIS type has no TKB counterpart (<c>shared.defaultTkbForUnknown</c>).</summary>
    public long DefaultTkb { get; }

    public IEnumerable<long> TkbTypes => _toDis.Keys;

    public bool TryTkbToDis(long tkb, out long packed) => _toDis.TryGetValue(tkb, out packed);

    /// <summary>
    /// The TKB type of a DIS type: the exact entry, else the entry that shares the longest prefix of the type (at least down to
    /// the category), the lowest TKB on a tie.
    /// </summary>
    public bool TryDisToTkb(long packed, out long tkb)
    {
        var wanted = DisTypeCode.Unpack(packed);
        int best = 3;
        tkb = 0;
        bool found = false;
        foreach (var (candidate, dis) in _all)
        {
            int score = DisTypeCode.CommonPrefix(wanted, dis);
            if (score > best || (score == best && found && candidate < tkb))
            {
                best = score;
                tkb = candidate;
                found = true;
            }
        }

        return found;
    }

    /// <summary>
    /// The DIS type of a HROT entity: the one the HROT pack provided when it is complete, else the catalogue entry of the TKB
    /// type. False when neither names a type.
    /// </summary>
    public bool TryResolveDisType(long tkb, long hrotProvided, out long packed)
    {
        if (DisTypeCode.IsCompletePacked(hrotProvided))
        {
            packed = hrotProvided;
            return true;
        }

        return TryTkbToDis(tkb, out packed);
    }

    /// <summary>True the first time this unknown TKB type is noted (the caller warns), false afterwards.</summary>
    public bool NoteUnknownTkb(long tkb) => _warnedTkb.Add(tkb);

    /// <summary>True the first time this unknown DIS type is noted (the caller warns), false afterwards.</summary>
    public bool NoteUnknownDis(long packed) => _warnedDis.Add(packed);
}

/// <summary>What the catalogue knows about a munition: its TKB type, its DIS munition type and the warhead and fuse (0 = other).</summary>
public readonly record struct MunitionInfo(long Tkb, long DisType, int Warhead, int Fuse);

/// <summary>
/// The munition catalogue in code (R-088): TKB munition &lt;-&gt; DIS munition type, with the warhead and fuse of the Fire and
/// Detonation PDUs. Warhead 1000 (high explosive) and fuse 8000 (point detonation) are SISO-REF-010 values to be checked
/// when the DIS egress uses them (H2b); a munition that is not in the table gets warhead and fuse 0 (other).
/// </summary>
public sealed class MunitionCatalog
{
    private static readonly MunitionInfo[] Entries =
    [
        new(501, new DisTypeCode(2, 9, 225, 2, 1, 1, 0).Pack(), 1000, 8000),
        new(502, new DisTypeCode(2, 9, 225, 2, 14, 1, 0).Pack(), 1000, 8000),
    ];

    private readonly Dictionary<long, MunitionInfo> _byTkb = new();
    private readonly List<MunitionInfo> _all = new();
    private readonly HashSet<long> _warnedTkb = new();
    private readonly HashSet<long> _warnedDis = new();

    public MunitionCatalog()
    {
        foreach (var entry in Entries)
        {
            _byTkb.Add(entry.Tkb, entry);
            _all.Add(entry);
        }
    }

    public IEnumerable<long> TkbTypes => _byTkb.Keys;

    public bool TryGet(long tkb, out MunitionInfo info) => _byTkb.TryGetValue(tkb, out info);

    /// <summary>The TKB munition of a DIS munition type: exact, else the longest shared prefix (at least down to the category).</summary>
    public bool TryDisToTkb(long packed, out long tkb)
    {
        var wanted = DisTypeCode.Unpack(packed);
        int best = 3;
        tkb = 0;
        bool found = false;
        foreach (var entry in _all)
        {
            int score = DisTypeCode.CommonPrefix(wanted, DisTypeCode.Unpack(entry.DisType));
            if (score > best || (score == best && found && entry.Tkb < tkb))
            {
                best = score;
                tkb = entry.Tkb;
                found = true;
            }
        }

        return found;
    }

    /// <summary>
    /// The munition as an event describes it: the DIS type the pack provided when it is complete, else the catalogue's; the warhead
    /// and fuse come from the catalogue entry of the TKB munition (0 when it has none).
    /// </summary>
    public MunitionInfo Describe(long tkb, long hrotProvidedDisType, out bool known)
    {
        known = _byTkb.TryGetValue(tkb, out var entry);
        long dis = DisTypeCode.IsCompletePacked(hrotProvidedDisType) ? hrotProvidedDisType : known ? entry.DisType : 0;
        return new MunitionInfo(tkb, dis, known ? entry.Warhead : 0, known ? entry.Fuse : 0);
    }

    public bool NoteUnknownTkb(long tkb) => _warnedTkb.Add(tkb);

    public bool NoteUnknownDis(long packed) => _warnedDis.Add(packed);
}
