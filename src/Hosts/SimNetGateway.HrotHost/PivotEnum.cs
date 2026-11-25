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
