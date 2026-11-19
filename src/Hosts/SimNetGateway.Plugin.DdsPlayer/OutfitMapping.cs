using SimNetGateway.Protocol.Hrot;
using D = SimNetGateway.Protocol.DdsPlayer;

namespace SimNetGateway.Plugin.DdsPlayer;

/// <summary>
/// A person's outfit message to the DPS <c>Appearance</c>: one slot per garment with its kind name and colour (0xRRGGBBAA), the camouflage and hair names, the face
/// traits and the flags. The kind names are HROT's own vocabulary (its outfit enumerations, by value); a value this table does not know is sent as its number, so a
/// newer HROT does not break a client. Equipment items (helmet, vest, weapon) are not in HROT's outfit yet and stay empty.
/// </summary>
internal static class OutfitMapping
{
    private static readonly string[] Top = ["Shirt", "TShirt", "Suit", "Robe", "Abaya", "Uniform", "Jacket", "Buckskin", "Perahan", "Chadari"];
    private static readonly string[] Over = ["None", "ChestRig", "BeadedYoke", "HairPipe", "YokeHairPipe"];
    private static readonly string[] Head = ["None", "Cap", "PoliceCap", "RiotHelmet", "Helmet", "Shemagh", "Skullcap", "Hijab", "Niqab", "ShemaghFaceWrap", "Headband", "Pakol", "Turban"];
    private static readonly string[] Extra = ["None", "Armbands", "Necklace", "ArmbandsNecklace", "Waistcoat", "Patu", "WaistcoatPatu"];
    private static readonly string[] Legwear = ["Trousers", "Shorts", "Skirt", "LongSkirt", "Hidden", "Breechcloth", "Tunban"];
    private static readonly string[] Hair = ["Short", "Long", "None", "Braids"];
    private static readonly string[] Camo = ["None", "Woodland", "Fleck", "Desert"];
    private static readonly string[] Beard = ["None", "Moustache", "Beard", "Paint"];

    public static D.Appearance ToAppearance(in HrotPersonAppearance m)
    {
        var slots = new List<D.AppearanceSlot>(13)
        {
            Slot("Skin", string.Empty, m.Skin),
            Slot("Top", Name(Top, m.TopKind), m.Top),
            Slot("Sleeve", string.Empty, m.Sleeve),
            Slot("Legs", Name(Legwear, m.Legwear), m.Bottom),
            Slot("Shin", string.Empty, m.Shin),
            Slot("Over", Name(Over, m.OverKind), m.OverColour),
            Slot("Head", Name(Head, m.HeadKind), m.HeadCloth),
            Slot("Hair", Name(Hair, m.HairStyle), m.Hair),
            Slot("Beard", Name(Beard, m.Beard), m.Hair),
            Slot("Extra", Name(Extra, m.ExtraKind), m.ExtraColour),
            Slot("Eyes", string.Empty, m.EyeColour),
            Slot("Lips", string.Empty, m.Lips),
        };
        if (m.Suit)
        {
            slots.Add(Slot("Tie", string.Empty, m.Tie));
        }

        return new D.Appearance
        {
            EntityId = m.Id,
            Seed = unchecked((uint)m.Seed),
            Side = m.Side,
            Slots = slots,
            Camouflage = Name(Camo, m.Camo),
            Hair = Name(Hair, m.HairStyle),
            FaceTraits = FormattableString.Invariant($"eyeSpacing={m.EyeSpacing:0.###};mouthHalfWidth={m.MouthHalfWidth:0.###};browHalfHeight={m.BrowHalfHeight:0.###}"),
            Sex = m.Woman ? D.Sex.SexFemale : D.Sex.SexMale,
            AgeClass = m.Child ? D.AgeClass.AgeClassChild : D.AgeClass.AgeClassAdult,
            IsSoldier = m.Soldier,
            IsChild = m.Child,
            IsWoman = m.Woman,
            IsPolice = m.Police,
            Equipment = [],
        };
    }

    private static D.AppearanceSlot Slot(string slot, string kind, uint colour) => new() { Slot = slot, Kind = kind, ColourRgba = colour };

    private static string Name(string[] names, byte value) => value < names.Length ? names[value] : value.ToString(System.Globalization.CultureInfo.InvariantCulture);
}
