using System.Globalization;
using System.Text;
using D = SimNetGateway.Protocol.DdsPlayer;

namespace SimNetGateway.DpsClient;

/// <summary>How a headless client joins (DD-023 3a): the first free placed unit, a named role with side / kit / area, or only observing.</summary>
public enum DpsJoinMode
{
    FirstUnit,
    Role,
    Observe,
}

/// <summary>The join the command line asked for. <see cref="Side"/> is the DIS force (0 = the offer's own), <see cref="Kit"/> the index into the role offer's kits, <see cref="Area"/> an area offer's name.</summary>
public sealed record DpsJoinPlan(DpsJoinMode Mode, string? Role = null, byte Side = 0, byte Kit = 0, string? Area = null)
{
    /// <summary>Builds the <c>JoinRequest</c> for this plan from the slot's offers; null with the reason in <paramref name="problem"/> when no request can be made now.</summary>
    public D.JoinRequest? Build(in D.PlayerSlot slot, out string? problem)
    {
        problem = null;
        var offers = slot.Offers;
        switch (Mode)
        {
            case DpsJoinMode.Observe:
                return new D.JoinRequest { Kind = D.JoinKind.JoinKindObserve, Role = string.Empty };
            case DpsJoinMode.FirstUnit:
                if (offers is not null)
                {
                    foreach (var offer in offers)
                    {
                        if (offer.Kind == D.OfferKind.OfferKindUnit && offer.Free)
                        {
                            return new D.JoinRequest { Kind = D.JoinKind.JoinKindUnit, UnitId = offer.Id, Role = string.Empty, Side = Side };
                        }
                    }
                }

                problem = "no free unit offer";
                return null;
        }

        D.Offer? role = Find(offers, D.OfferKind.OfferKindRole, Role);
        if (role is not { } r)
        {
            problem = $"no role offer named '{Role}'";
            return null;
        }

        if (!r.Free)
        {
            problem = $"role '{r.Name}' is full ({r.Count}/{r.Max})";
            return null;
        }

        int areaId = 0;
        if (Area is not null)
        {
            if (Find(offers, D.OfferKind.OfferKindArea, Area) is not { } area)
            {
                problem = $"no area offer named '{Area}'";
                return null;
            }

            areaId = area.Id;
        }

        return new D.JoinRequest { Kind = D.JoinKind.JoinKindRole, UnitId = r.Id, Role = r.Name ?? string.Empty, Side = Side, Kit = Kit, AreaId = areaId };
    }

    private static D.Offer? Find(List<D.Offer>? offers, D.OfferKind kind, string? name)
    {
        if (offers is null)
        {
            return null;
        }

        foreach (var offer in offers)
        {
            if (offer.Kind == kind && string.Equals(offer.Name, name, StringComparison.OrdinalIgnoreCase))
            {
                return offer;
            }
        }

        return null;
    }
}

/// <summary>The <c>--list-offers</c> text: one line per offer, invariant culture.</summary>
public static class DpsOfferList
{
    public static string Kind(D.OfferKind kind) => kind switch
    {
        D.OfferKind.OfferKindUnit => "unit",
        D.OfferKind.OfferKindRole => "role",
        D.OfferKind.OfferKindArea => "area",
        _ => kind.ToString(),
    };

    public static string Line(in D.Offer o)
    {
        var sb = new StringBuilder();
        string kits = o.Kits is { Count: > 0 } k ? string.Join(',', k.Select(n => n.Name)) : string.Empty;
        sb.Append(CultureInfo.InvariantCulture, $"offer kind={Kind(o.Kind)} id={o.Id} name=\"{o.Name}\" side={o.Side} free={(o.Free ? "true" : "false")} count={o.Count}/{o.Max} kits=[{kits}] outline_points={o.Outline?.Count ?? 0}");
        return sb.ToString();
    }

    public static IEnumerable<string> Lines(in D.PlayerSlot slot)
    {
        var list = new List<string>();
        if (slot.Offers is not null)
        {
            foreach (var offer in slot.Offers)
            {
                list.Add(Line(offer));
            }
        }

        return list;
    }
}
