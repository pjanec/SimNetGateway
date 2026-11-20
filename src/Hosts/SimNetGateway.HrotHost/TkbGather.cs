using System.Text.Json.Nodes;
using Fdp.Interfaces;
using Fdp.Toolkit.Diagnostics;
using Fdp.Toolkit.Tkb.Domain;
using SimNetGateway.Engine.InMemory;
using SimNetGateway.Protocol.Hrot;

// DPS round 2 (DD-018 3.2): the TKB types the running simulation has, as its own serialization of their presentation descriptors, once per type per runtime.
namespace SimNetGateway.HrotHost;

/// <summary>
/// The one place that says which TKB descriptors a client may see: the presentation data (how a thing looks, sounds, moves and is armed), never the AI, damage or
/// scenario tables. Matched by the descriptor's CLR type, so a rename in HROT breaks the build instead of silently dropping a descriptor.
/// </summary>
internal static class TkbPresentation
{
    public static readonly IReadOnlySet<Type> Descriptors = new HashSet<Type>
    {
        typeof(MunitionVisualDto), typeof(WarheadDto), typeof(MunitionEffectsDto),
        typeof(WeaponSuiteDto), typeof(WeaponSystemDto), typeof(WeaponCapabilitiesDto), typeof(TurretSuiteDto),
        typeof(VehicleOpeningsDto), typeof(VehicleLightsDto), typeof(VehicleParametersDto), typeof(CarrierDefDto),
        typeof(BodyGeometryDto), typeof(PersonDto), typeof(OutfitDto), typeof(VisualDefinitionDto),
        typeof(EffectVisualDto), typeof(SoundDto), typeof(SoundSetDto),
        typeof(LocomotionDto), typeof(StrideRenderModelDefDto), typeof(CharacterAnimationDefDto), typeof(FlightParamsDto),
    };

    /// <summary>The JSON of one template in the shape of HROT's debug API type record (<c>GET /tkb/types/{id}</c>), restricted to <see cref="Descriptors"/> (<paramref name="serialize"/> is HROT'''s own serializer unless a test replaces it).</summary>
    public static string JsonOf(TkbTemplate t, Func<object?, string>? serialize = null)
    {
        var descriptors = new JsonArray();
        serialize ??= data => EventSerializationHelper.SerializeToJson(data);
        foreach (var (type, partId, data) in t.GetAllDescriptors())
        {
            if (!Descriptors.Contains(type))
            {
                continue;
            }

            // a descriptor that fails to serialize stays in the list and says so, as HROT's debug API does (an absent one cannot be told from a broken one otherwise)
            try
            {
                descriptors.Add(new JsonObject { ["type"] = type.Name, ["partId"] = partId, ["data"] = JsonNode.Parse(serialize(data)) });
            }
            catch (Exception ex)
            {
                descriptors.Add(new JsonObject { ["type"] = type.Name, ["partId"] = partId, ["serializationError"] = $"{ex.GetType().Name}: {ex.Message}" });
            }
        }

        var root = new JsonObject
        {
            ["tkbType"] = (long)t.TkbType,
            ["name"] = t.Name,
            ["categoryPath"] = t.CategoryPath,
            ["disType"] = t.DisType.ToString(),
            ["descriptors"] = descriptors,
        };
        return root.ToJsonString();
    }
}

/// <summary>
/// Collects the TKB type ids referenced by what the adapter publishes (entity types, fire and detonation munitions, rounds, loaded weapon ammunition) and publishes each
/// type's record once. An id the TKB does not know is remembered as missing and not asked again. Cleared at a world boundary. Main thread only.
/// </summary>
internal sealed class TkbGather
{
    private readonly HashSet<long> _sent = new(), _missing = new(), _pending = new();

    public void Clear()
    {
        _sent.Clear();
        _missing.Clear();
        _pending.Clear();
    }

    public void Reference(long type)
    {
        if (type != 0 && !_sent.Contains(type) && !_missing.Contains(type))
        {
            _pending.Add(type);
        }
    }

    public void Flush(ITkbDatabase? tkb, InMemoryInbound inbound)
    {
        if (_pending.Count == 0)
        {
            return;
        }

        foreach (long type in _pending)
        {
            if (tkb is null)
            {
                continue;   // no database yet: ask again when something references the type
            }

            if (!tkb.TryGetByType(type, out var template))
            {
                _missing.Add(type);
                continue;
            }

            _sent.Add(type);
            inbound.Enqueue(new HrotTypeRecord(type, template.Name, template.CategoryPath ?? string.Empty, template.DisType.ToString(), TkbPresentation.JsonOf(template)));
        }

        _pending.Clear();
    }
}
