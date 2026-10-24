using SimNetGateway.Common;
using SimNetGateway.Engine;
using SimNetGateway.Protocol.Ned;

namespace SimNetGateway.Gateway.NedRpr;

/// <summary>NedWeaponStatus: stores the selected munition and the ammunition (R-027).</summary>
public sealed class NedWeaponStatusIngress : IGatewayModule
{
    private readonly PivotSchema _schema;
    private readonly NedRprMaps _maps;

    public NedWeaponStatusIngress(PivotSchema schema, NedRprMaps maps)
    {
        _schema = schema;
        _maps = maps;
    }

    public string Name => nameof(NedWeaponStatusIngress);

    public void Register(ModuleSetup setup)
    {
        setup.Produces(_schema.SelectedMunition, _schema.Ammo);
        setup.OnMessage<NedWeaponStatus>(Handle);
    }

    private void Handle(in NedWeaponStatus m, IngressContext ctx)
    {
        var resolution = IngressEntities.ResolveNed(ctx, _maps, m.Id, m.Member, create: true, out var id);
        if (resolution is EntityResolution.Echo or EntityResolution.Missing)
        {
            return;
        }

        ctx.Changes.Set(id, _schema.SelectedMunition, AttrValue.From(m.SelectedMunitionTkb));
        ctx.Changes.Set(id, _schema.Ammo, AttrValue.From(m.Ammo));
    }
}
