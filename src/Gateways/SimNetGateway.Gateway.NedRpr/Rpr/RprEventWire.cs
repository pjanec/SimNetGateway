using SimNetGateway.Engine;
using SimNetGateway.Protocol.Rpr;

namespace SimNetGateway.Gateway.NedRpr;

/// <summary>Shared by the RPR event egress modules: the munition type and the handles of an event's entities.</summary>
internal static class RprEventWire
{
    /// <summary>The DIS munition type: the event's, else (when allowed) the shooter's stored selection, else the default plus a warning.</summary>
    public static DisEntityType Munition(PivotSchema schema, TypeCatalog catalog, IGatewayContext gateway, string origin,
        AttributeBag payload, bool useSelected)
    {
        int tkb = payload.TryGet(schema.EventMunitionTkb, out var given) ? (int)given.AsInt() : 0;
        if (tkb == 0 && useSelected && payload.TryGet(schema.EventShooter, out var shooter) &&
            gateway.State.Find(shooter.AsEntity()) is { } record && record.TryGet(schema.SelectedMunition, out var selected))
        {
            tkb = (int)selected.AsInt();
        }

        if (tkb == 0)
        {
            gateway.Diagnostics.Warn(origin, "No munition given and none stored for the shooter; using the default DIS type.");
            return TypeCatalog.DefaultDis;
        }

        if (catalog.TryToDis(tkb, out var dis))
        {
            return dis;
        }

        if (catalog.NoteUnknownTkb(tkb))
        {
            gateway.Diagnostics.Warn(origin, $"Unknown munition TKB type {tkb}; using the default DIS type.");
        }

        return TypeCatalog.DefaultDis;
    }

    public static bool TryEntity(AttributeBag payload, AttrId attr, out EntityId entity)
    {
        if (payload.TryGet(attr, out var value))
        {
            entity = value.AsEntity();
            return true;
        }

        entity = default;
        return false;
    }

    public static RprObjectHandle? HandleOf(NedRprMaps maps, AttributeBag payload, AttrId attr) =>
        payload.TryGet(attr, out var entity) && maps.RprHandle.TryGetKey(entity.AsEntity(), out var handle) ? handle : null;
}
