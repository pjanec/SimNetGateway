using SimNetGateway.Engine;
using SimNetGateway.Protocol.Display;

namespace SimNetGateway.Gateway.NedRpr;

/// <summary>
/// The display side does not understand dead reckoning (R-029): it gets positions only through the derived
/// <c>Derived.PredictedPosition</c> that the DR processor writes. Rule "Position" labels the entity on its first position
/// (Identity.Name when free, else "E" plus a counter - never the EntityId) and sends the position in degrees with the local
/// heading; rule "Remove" tells the display that a labelled entity is gone.
/// </summary>
public sealed class DisplayPositionEgress : IGatewayModule
{
    private readonly PivotSchema _schema;
    private readonly NedRprMaps _maps;
    private readonly IOutboundChannel<IDisplayMessage> _output;
    private long _anonymous;

    public DisplayPositionEgress(PivotSchema schema, NedRprMaps maps, IOutboundChannel<IDisplayMessage> output)
    {
        _schema = schema;
        _maps = maps;
        _output = output;
    }

    public string Name => nameof(DisplayPositionEgress);

    public void Register(ModuleSetup setup)
    {
        setup.Consumes(_schema.Name, _schema.Orientation, _schema.PredictedAt);
        setup.OnAttributesChanged(_schema.PredictedPosition)
            .Named("Position")
            .Do(OnPosition);
        setup.OnEntityRemoved()
            .Named("Remove")
            .Do(t =>
            {
                if (_maps.DisplayLabel.TryGetKey(t.Id, out var label))
                {
                    _output.Send(new DisplayEntityRemoved(label));
                }
            });
    }

    private void OnPosition(EntityTrigger t)
    {
        var entity = t.Entity;
        if (!entity.TryGet(_schema.PredictedPosition, out var predicted))
        {
            return;
        }

        if (!_maps.DisplayLabel.TryGetKey(t.Id, out var label))
        {
            if (!entity.TryGet(_schema.Name, out var name))
            {
                return; // the display can only show entities that have a name
            }

            label = name.AsString();
            if (_maps.DisplayLabel.TryResolve(label, out _) || !_maps.DisplayLabel.Bind(t.Id, label))
            {
                do
                {
                    label = "E" + (++_anonymous);
                }
                while (_maps.DisplayLabel.TryResolve(label, out _));

                _maps.DisplayLabel.Bind(t.Id, label);
            }
        }

        var position = predicted.AsVec3();
        NedRprConversions.EcefToGeodetic(position, out double latitude, out double longitude, out double altitude);
        double heading = entity.TryGet(_schema.Orientation, out var orientation)
            ? NedRprConversions.LocalHeadingDeg(position, orientation.AsVec3())
            : 0.0;
        _output.Send(new DisplayEntityPosition(label, latitude, longitude, altitude, heading));
    }
}
