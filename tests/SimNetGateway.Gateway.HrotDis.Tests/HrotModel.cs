using SimNetGateway.Protocol.Hrot;

namespace SimNetGateway.Gateway.HrotDis.Tests;

/// <summary>
/// The test HROT side's view of descriptor ownership (DD-012 section 4a item 7): the set of groups of each HROT entity it was told are
/// owned outside HROT, and the values it adopted from <see cref="ExternalState"/>. It stands for the pack until the private-repo pack exists.
/// Its states keep being sent in full (<see cref="HrotDisHarness.State"/>): the gateway ingress is what leaves external groups out.
/// </summary>
public sealed class HrotModel
{
    private readonly Dictionary<int, HrotDescriptor> _external = new();
    private readonly Dictionary<int, AdoptedValues> _adopted = new();
    private readonly List<string> _violations = new();

    /// <summary>What HROT adopted from the external messages for one entity (fields of groups never received stay default).</summary>
    public sealed record AdoptedValues(HrotDrSnapshot? State, string? Marking, int? Force, double? HealthCurrent, double? HealthMax);

    /// <summary>Every message the gateway sent about descriptors, in order.</summary>
    public List<IGatewayToHrotMessage> Received { get; } = new();

    /// <summary>The groups HROT currently believes are owned outside HROT for the entity.</summary>
    public HrotDescriptor ExternalOf(int id) => _external.TryGetValue(id, out var set) ? set : HrotDescriptor.None;

    /// <summary>The values HROT adopted for the entity, or null when it adopted none.</summary>
    public AdoptedValues? AdoptedOf(int id) => _adopted.TryGetValue(id, out var v) ? v : null;

    /// <summary>Protocol violations seen by the model itself (an ExternalState for a group that is not external).</summary>
    public IReadOnlyList<string> Violations => _violations;

    /// <summary>HROT removed the entity: it forgets what it knew.</summary>
    public void Forget(int id)
    {
        _external.Remove(id);
        _adopted.Remove(id);
    }

    /// <summary>Applies a message the gateway sent.</summary>
    public void OnSent(IGatewayToHrotMessage message)
    {
        switch (message)
        {
            case HrotDescriptorOwnership o:
                Received.Add(o);
                if (o.External == HrotDescriptor.None)
                {
                    _external.Remove(o.Id);
                }
                else
                {
                    _external[o.Id] = o.External;
                }

                // a group handed back is local again: what was adopted for it is dropped
                if (_adopted.TryGetValue(o.Id, out var kept))
                {
                    _adopted[o.Id] = kept with
                    {
                        State = (o.External & HrotDescriptor.Spatial) != 0 ? kept.State : null,
                        Marking = (o.External & HrotDescriptor.Info) != 0 ? kept.Marking : null,
                        Force = (o.External & HrotDescriptor.Info) != 0 ? kept.Force : null,
                        HealthCurrent = (o.External & HrotDescriptor.Damage) != 0 ? kept.HealthCurrent : null,
                        HealthMax = (o.External & HrotDescriptor.Damage) != 0 ? kept.HealthMax : null,
                    };
                }

                break;
            case ExternalState e:
                Received.Add(e);
                var notExternal = e.Descriptors & ~ExternalOf(e.Id);
                if (notExternal != HrotDescriptor.None)
                {
                    _violations.Add($"ExternalState for entity {e.Id} names {notExternal}, which is not external");
                }

                if (e.Descriptors == HrotDescriptor.None)
                {
                    _violations.Add($"ExternalState for entity {e.Id} names no group");
                }

                _adopted.TryGetValue(e.Id, out var current);
                current ??= new AdoptedValues(null, null, null, null, null);
                if ((e.Descriptors & HrotDescriptor.Spatial) != 0)
                {
                    current = current with { State = e.State };
                }

                if ((e.Descriptors & HrotDescriptor.Info) != 0)
                {
                    current = current with { Marking = e.Marking, Force = e.Force };
                }

                if ((e.Descriptors & HrotDescriptor.Damage) != 0)
                {
                    current = current with { HealthCurrent = e.HealthCurrent, HealthMax = e.HealthMax };
                }

                _adopted[e.Id] = current;
                break;
        }
    }
}
