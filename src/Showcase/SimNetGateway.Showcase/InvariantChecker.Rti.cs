using SimNetGateway.Protocol.Rpr;
using SimNetGateway.Protocol.Rpr.Fom;

namespace SimNetGateway.Showcase;

// G-9 (DD-002 section 6, added in HLA-3, DD-008 section 12b): what the gateway sent to the RPR side is what the RPR side received.
public sealed partial class InvariantChecker
{
    private readonly Dictionary<RprObjectHandle, RprObjectUpdate> _sentState = new();
    private readonly HashSet<RprObjectHandle> _touchedNow = new();
    private readonly HashSet<RprObjectHandle> _carriedNow = new();
    private readonly List<RprObjectHandle> _removedNow = new();
    private readonly List<RprWeaponFire> _firesNow = new();
    private readonly List<RprMunitionDetonation> _detonationsNow = new();
    private readonly Dictionary<Guid, EventIdentifierStruct> _eventIdentifiers = new();
    private int _registrationsSent;
    private int _damagePercentSent;
    private int _controlsSent;

    /// <summary>
    /// G-9 on: every tick, what the gateway sent to the RPR side during the tick is compared with what the scripted federate
    /// received through the RTI and decoded on its own (<see cref="RtiMirror"/>), and the transport's loss counters must be zero.
    /// Off by default: it needs the host's tick to end with <see cref="NedRprHost.EvokePeer"/> before <see cref="Evaluate"/>, and a
    /// decoding federate.
    /// </summary>
    public bool CheckRti { get; set; }

    /// <summary>The state the gateway has described an object in so far (its updates merged), or null when it never sent one. Needs <see cref="CheckRti"/>.</summary>
    public RprObjectUpdate? SentState(RprObjectHandle handle) => _sentState.TryGetValue(handle, out var state) ? state : null;

    /// <summary>
    /// What differs between everything the gateway ever sent about an object and what the federate holds of it now (empty = the
    /// RPR side sees exactly what the gateway said). Needs <see cref="CheckRti"/>.
    /// </summary>
    public IReadOnlyList<string> MirrorDifferences(RprObjectHandle handle)
    {
        if (!_sentState.TryGetValue(handle, out var sent))
        {
            return ["the gateway never sent an update for handle " + handle.Value];
        }

        return _h.RprPeer.DiscoveredObject(handle.Value) is { } seen
            ? RtiMirror.Differences(sent, seen)
            : ["the RPR federate has no object with handle " + handle.Value];
    }

    private void MirrorSent(IRprMessage message)
    {
        switch (message)
        {
            case RprRegisterObject:
                _registrationsSent++;
                break;
            case RprObjectUpdate m:
                _sentState[m.Handle] = RtiMirror.Merge(_sentState.GetValueOrDefault(m.Handle), m);
                _touchedNow.Add(m.Handle);
                if (RtiMirror.CarriesAttributes(m))
                {
                    _carriedNow.Add(m.Handle);
                }

                if (m.DamagePercent is not null)
                {
                    _damagePercentSent++;
                }

                break;
            case RprObjectRemoved m:
                _removedNow.Add(m.Handle);
                break;
            case RprWeaponFire m:
                _firesNow.Add(m);
                break;
            case RprMunitionDetonation m:
                _detonationsNow.Add(m);
                break;
            case RprSimulationControl:
                _controlsSent++;
                break;
        }
    }

    private void CheckRtiMirror()
    {
        var peer = _h.RprPeer;
        long tick = _h.Engine.TickCount;

        // every registration reached the federate as a discovery (the registration is a service call, the discovery its effect in the same evoke)
        if (peer.Discovered.Count != _registrationsSent)
        {
            Violate("G-9", $"the gateway registered {_registrationsSent} objects, the RPR federate discovered {peer.Discovered.Count}");
        }

        foreach (var handle in _touchedNow)
        {
            if (_removedNow.Contains(handle))
            {
                continue;
            }

            var seen = peer.DiscoveredObject(handle.Value);
            if (seen is null)
            {
                Violate("G-9", $"the gateway updated handle {handle.Value}, which the RPR federate never discovered");
                continue;
            }

            if (_carriedNow.Contains(handle) && seen.LastReflectTick != tick)
            {
                Violate("G-9", $"the update of handle {handle.Value} was reflected at tick {seen.LastReflectTick}, not in the tick it was sent ({tick})");
            }

            foreach (string difference in RtiMirror.Differences(_sentState[handle], seen))
            {
                Violate("G-9", $"handle {handle.Value} ({seen.Marking?.AsciiText}): {difference}");
            }
        }

        foreach (var handle in _removedNow)
        {
            if (!peer.RemovedHandles.Any(r => r.Handle == (int)handle.Value && r.Tick == tick))
            {
                Violate("G-9", $"the removal of handle {handle.Value} did not reach the RPR federate in the tick it was sent ({tick})");
            }
        }

        var fires = peer.Fires.Where(f => f.Tick == tick).ToList();
        if (fires.Count != _firesNow.Count)
        {
            Violate("G-9", $"the gateway sent {_firesNow.Count} weapon fires, the RPR federate received {fires.Count}");
        }
        else
        {
            for (int i = 0; i < fires.Count; i++)
            {
                foreach (string difference in RtiMirror.Differences(_firesNow[i], fires[i], peer))
                {
                    Violate("G-9", $"weapon fire {MessageText.Short(_firesNow[i].EventId)}: {difference}");
                }

                CheckEventIdentifier(_firesNow[i].EventId, fires[i].Event, "weapon fire");
            }
        }

        var detonations = peer.Detonations.Where(d => d.Tick == tick).ToList();
        if (detonations.Count != _detonationsNow.Count)
        {
            Violate("G-9", $"the gateway sent {_detonationsNow.Count} detonations, the RPR federate received {detonations.Count}");
        }
        else
        {
            for (int i = 0; i < detonations.Count; i++)
            {
                foreach (string difference in RtiMirror.Differences(_detonationsNow[i], detonations[i], peer))
                {
                    Violate("G-9", $"detonation {MessageText.Short(_detonationsNow[i].EventId)}: {difference}");
                }

                CheckEventIdentifier(_detonationsNow[i].EventId, detonations[i].Event, "detonation");
            }
        }

        // nothing was lost on the way: the transport counted no error, and the two things the FOM cannot carry were counted as dropped
        var transport = _h.RprConnection;
        if (transport.EncodeErrors + transport.SendErrors + transport.RejectedOutbound + transport.DecodeErrors + transport.CallbackErrors +
            transport.UnknownObjectCallbacks != 0)
        {
            Violate("G-9", $"the transport lost something: encode {transport.EncodeErrors}, send {transport.SendErrors}, rejected {transport.RejectedOutbound}, " +
                $"decode {transport.DecodeErrors}, callback {transport.CallbackErrors}, unknown object {transport.UnknownObjectCallbacks}");
        }

        if (transport.DroppedDamagePercent != _damagePercentSent || transport.DroppedSimulationControl != _controlsSent)
        {
            Violate("G-9", $"damage percentages sent {_damagePercentSent} / dropped {transport.DroppedDamagePercent}, " +
                $"simulation controls sent {_controlsSent} / dropped {transport.DroppedSimulationControl}");
        }

        if (peer.AttributeDecodeFailures.Count + peer.InteractionDecodeFailures.Count != 0)
        {
            Violate("G-9", $"the RPR federate could not decode {peer.AttributeDecodeFailures.Count} attribute and {peer.InteractionDecodeFailures.Count} parameter values");
        }

        _touchedNow.Clear();
        _carriedNow.Clear();
        _removedNow.Clear();
        _firesNow.Clear();
        _detonationsNow.Clear();
    }

    /// <summary>One gateway event id is one RTI event identifier: the same id always arrives as the same identifier, two ids never share one.</summary>
    private void CheckEventIdentifier(Guid id, EventIdentifierStruct arrived, string what)
    {
        if (_eventIdentifiers.TryGetValue(id, out var known))
        {
            if (known != arrived)
            {
                Violate("G-9", $"the {what} {MessageText.Short(id)} arrived as event identifier {arrived}, earlier as {known}");
            }

            return;
        }

        foreach (var (other, identifier) in _eventIdentifiers)
        {
            if (identifier == arrived)
            {
                Violate("G-9", $"the events {MessageText.Short(id)} and {MessageText.Short(other)} arrived with the same event identifier {arrived}");
            }
        }

        _eventIdentifiers[id] = arrived;
    }
}
