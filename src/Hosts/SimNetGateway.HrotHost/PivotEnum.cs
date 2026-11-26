using System.Runtime.CompilerServices;
using SimNetGateway.Protocol.Hrot;

namespace SimNetGateway.HrotHost;

/// <summary>
/// T-080: HROT's enumerations into the pivot's enums BY NAME, without a string at run time. For each (HROT enum, pivot enum) pair a table indexed by HROT's value is built ONCE from the member NAMES
/// (so a member HROT appends or reorders is never mislabelled by number); an unknown member is the pivot's fallback (its value 0 - Unknown / None - unless <see cref="Fallbacks"/> says otherwise).
/// A pivot enum mirrors the DPS enum it ends in, so a member HROT spells differently is a <see cref="Renames"/> row here, in one place. A lookup is an array read: 0 B.
/// </summary>
internal static class PivotEnum<TPivot>
    where TPivot : struct, Enum
{
    /// <summary>The pivot member of HROT's value <paramref name="value"/> (any HROT enum whose underlying type is 1, 2 or 4 bytes wide).</summary>
    public static TPivot Of<THrot>(THrot value)
        where THrot : struct, Enum
    {
        var table = Cache<THrot>.Table;
        int index = Unsafe.SizeOf<THrot>() switch
        {
            1 => Unsafe.As<THrot, byte>(ref value),
            2 => Unsafe.As<THrot, short>(ref value),
            _ => Unsafe.As<THrot, int>(ref value),
        };
        return (uint)index < (uint)table.Length ? table[index] : Cache<THrot>.Fallback;
    }

    private static class Cache<THrot>
        where THrot : struct, Enum
    {
        public static readonly TPivot Fallback = PivotEnums.FallbackOf<TPivot>();

        public static readonly TPivot[] Table = Build();

        private static TPivot[] Build()
        {
            var values = Enum.GetValues<THrot>();
            int max = 0;
            foreach (var v in values)
            {
                max = Math.Max(max, Convert.ToInt32(v));
            }

            var table = new TPivot[max + 1];
            Array.Fill(table, Fallback);
            foreach (var v in values)
            {
                string name = v.ToString();
                string mapped = PivotEnums.Renames.TryGetValue((typeof(TPivot), name), out var renamed) ? renamed : name;
                // the table has the fallback for a name the pivot does not have (a value HROT appends, or HROT's own "none" spelled differently)
                table[Convert.ToInt32(v)] = Enum.TryParse<TPivot>(mapped, out var p) && Enum.IsDefined(p) && p.ToString() == mapped ? p : Fallback;
            }

            return table;
        }
    }
}

/// <summary>
/// T-081: the reverse of <see cref="PivotEnum{TPivot}"/>, the pivot's enum into HROT's BY NAME (the client to HROT direction), through a table built ONCE (an array read: 0 B). It uses the same
/// <see cref="PivotEnums.Renames"/> list inverted: the HROT member whose name IS the pivot member's wins, else the first HROT member (lowest value) a rename row points at the pivot member (several HROT
/// members may share one pivot member, e.g. Tracer / Flare / Chaff to Omitted). A pivot member HROT has no member for (and a value no member has) is HROT's value 0 - callers that must tell it from a real
/// value 0 check <see cref="Has"/>.
/// </summary>
internal static class HrotEnum<THrot>
    where THrot : struct, Enum
{
    /// <summary>The HROT member of the pivot value <paramref name="value"/> (HROT's value 0 when HROT has none).</summary>
    public static THrot Of<TPivot>(TPivot value)
        where TPivot : struct, Enum
    {
        var table = Cache<TPivot>.Table;
        int index = Unsafe.SizeOf<TPivot>() switch
        {
            1 => Unsafe.As<TPivot, byte>(ref value),
            2 => Unsafe.As<TPivot, short>(ref value),
            _ => Unsafe.As<TPivot, int>(ref value),
        };
        return (uint)index < (uint)table.Length ? table[index].Value : default;
    }

    /// <summary>Whether HROT has a member for the pivot value <paramref name="value"/>.</summary>
    public static bool Has<TPivot>(TPivot value)
        where TPivot : struct, Enum
    {
        var table = Cache<TPivot>.Table;
        int index = Unsafe.SizeOf<TPivot>() switch
        {
            1 => Unsafe.As<TPivot, byte>(ref value),
            2 => Unsafe.As<TPivot, short>(ref value),
            _ => Unsafe.As<TPivot, int>(ref value),
        };
        return (uint)index < (uint)table.Length && table[index].Found;
    }

    private static class Cache<TPivot>
        where TPivot : struct, Enum
    {
        public static readonly (THrot Value, bool Found)[] Table = Build();

        private static (THrot, bool)[] Build()
        {
            var pivotValues = Enum.GetValues<TPivot>();
            int max = 0;
            foreach (var v in pivotValues)
            {
                max = Math.Max(max, Convert.ToInt32(v));
            }

            var table = new (THrot, bool)[max + 1];
            var exact = new bool[max + 1];
            foreach (var h in Enum.GetValues<THrot>())
            {
                string name = h.ToString();
                bool renamed = PivotEnums.Renames.TryGetValue((typeof(TPivot), name), out var target);
                string mapped = renamed ? target! : name;
                if (!Enum.TryParse<TPivot>(mapped, out var p) || !Enum.IsDefined(p) || p.ToString() != mapped)
                {
                    continue;
                }

                int at = Convert.ToInt32(p);
                // a same-named member always wins; a renamed one only fills a slot nothing has taken (lowest HROT value first, as Enum.GetValues orders)
                if (!renamed)
                {
                    table[at] = (h, true);
                    exact[at] = true;
                }
                else if (!table[at].Item2)
                {
                    table[at] = (h, true);
                }
            }

            return table;
        }
    }
}

/// <summary>The places where HROT's member name and the contract's differ, and the pivot enums whose unknown value is not their value 0.</summary>
internal static class PivotEnums
{
    public static readonly Dictionary<(Type Pivot, string Hrot), string> Renames = new()
    {
        [(typeof(HrotEffectSize), "Huge")] = nameof(HrotEffectSize.Large),                  // DPS has three sizes: a huge calibre is drawn large
        [(typeof(HrotImpactKind), "Entry")] = nameof(HrotImpactKind.Entered),
        [(typeof(HrotImpactKind), "Exit")] = nameof(HrotImpactKind.Left),
        [(typeof(HrotImpactKind), "Stop")] = nameof(HrotImpactKind.Stopped),
        [(typeof(HrotViewMode), "FixedKey")] = nameof(HrotViewMode.Fixed),                  // the contract's name for HROT's FixedKey
        [(typeof(HrotDeckPartKind), "Slab")] = nameof(HrotDeckPartKind.Deck),               // HROT's TerrainWalkableKind names the deck slab Slab
        [(typeof(HrotEffectKind), "Decal")] = nameof(HrotEffectKind.GroundMark),
        [(typeof(HrotEffectKind), "Fire")] = nameof(HrotEffectKind.WreckFlame),
        [(typeof(HrotEffectKind), "Smoke")] = nameof(HrotEffectKind.WreckSmoke),
        [(typeof(HrotEffectKind), "Tracer")] = nameof(HrotEffectKind.Omitted),              // DPS deliberately does not carry these
        [(typeof(HrotEffectKind), "Flare")] = nameof(HrotEffectKind.Omitted),
        [(typeof(HrotEffectKind), "Chaff")] = nameof(HrotEffectKind.Omitted),
    };

    public static TPivot FallbackOf<TPivot>()
        where TPivot : struct, Enum
        => typeof(TPivot) == typeof(HrotKillKind) ? (TPivot)(object)HrotKillKind.Unknown : default;   // "destroyed, kind unknown"; HROT's "None" is its own name
}
