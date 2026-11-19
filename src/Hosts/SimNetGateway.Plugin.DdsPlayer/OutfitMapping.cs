using SimNetGateway.Protocol.Hrot;
using D = SimNetGateway.Protocol.DdsPlayer;

namespace SimNetGateway.Plugin.DdsPlayer;

/// <summary>
/// A person's outfit message to the DPS <c>Appearance</c>: one slot per garment with its kind name and colour (0xRRGGBBAA), the camouflage and hair names, the face
/// traits and the flags. The kind names arrive in the message, written by the host from HROT's own enumerations; nothing of HROT's vocabulary is kept here. Equipment
/// items (helmet, vest, weapon) are not in HROT's outfit yet and stay empty.
/// </summary>
internal static class OutfitMapping
{
    public static D.Appearance ToAppearance(in HrotPersonAppearance m)
    {
        var slots = new List<D.AppearanceSlot>(13)
        {
            Slot("Skin", string.Empty, m.Skin),
            Slot("Top", m.TopKind, m.Top),
            Slot("Sleeve", string.Empty, m.Sleeve),
            Slot("Legs", m.Legwear, m.Bottom),
            Slot("Shin", string.Empty, m.Shin),
            Slot("Over", m.OverKind, m.OverColour),
            Slot("Head", m.HeadKind, m.HeadCloth),
            Slot("Hair", m.HairStyle, m.Hair),
            Slot("Beard", m.Beard, m.Hair),
            Slot("Extra", m.ExtraKind, m.ExtraColour),
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
            Camouflage = m.Camo,
            Hair = m.HairStyle,
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
}
