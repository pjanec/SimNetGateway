namespace SimNetGateway.Architecture.Tests;

/// <summary>The layers of DD-003 section 2; a project's layer is given by its folder under <c>src/</c>.</summary>
public enum Layer
{
    /// <summary><c>src/Engine/SimNetGateway.Engine</c> - the engine core.</summary>
    Engine,

    /// <summary>Other projects in <c>src/Engine/</c>, e.g. <c>Engine.InMemory</c>.</summary>
    EngineSupport,

    /// <summary><c>src/Common/</c> - vocabulary-free building blocks for modules.</summary>
    Common,

    /// <summary><c>src/Protocols/</c> - pure wire models.</summary>
    Protocol,

    /// <summary><c>src/Gateways/</c> - one concrete gateway each.</summary>
    Gateway,

    /// <summary><c>src/Transports/</c> - one network binding each.</summary>
    Transport,

    /// <summary><c>src/Showcase/</c> - the DD-002 harness.</summary>
    Showcase,

    /// <summary><c>src/Hosts/</c> - the only place where all layers meet.</summary>
    Host,
}

/// <summary>A project reference as seen by the rules: the referenced name and its layer, if known.</summary>
public readonly record struct Reference(string Name, Layer? Layer);

/// <summary>The dependency rules of DD-003 section 3 (R-049, R-050), as data plus one check.</summary>
public static class LayerRules
{
    public const string EngineProject = "SimNetGateway.Engine";

    private static readonly Dictionary<Layer, Layer[]> Allowed = new()
    {
        // rule 1: the engine references nothing but the BCL
        [Layer.Engine] = [],
        // Engine.InMemory: channels and clocks for the engine's interfaces
        [Layer.EngineSupport] = [Layer.Engine],
        // rule 3: Common references only the engine
        [Layer.Common] = [Layer.Engine],
        // rule 2: protocol projects reference nothing, not even the engine
        [Layer.Protocol] = [],
        // rule 4: engine, Common and the protocols it connects - never a transport, host or other gateway
        [Layer.Gateway] = [Layer.Engine, Layer.Common, Layer.Protocol],
        // rule 5: the engine and one protocol - never a gateway
        [Layer.Transport] = [Layer.Engine, Layer.Protocol],
        // the harness drives a gateway through in-memory channels (DD-002 section 8) and, since HLA-3 (DD-008 section 12b, T-021), wires
        // the RPR side to the HLA transport on the fake RTI: the showcase assembly is the shared host assembly (NedRprHost) of the
        // demo, the viewer and the tests, so the wiring lives once, here (a host cannot reference another host)
        [Layer.Showcase] = [Layer.Engine, Layer.EngineSupport, Layer.Common, Layer.Protocol, Layer.Gateway, Layer.Transport],
        // rule 6: only hosts reference gateways together with transports
        [Layer.Host] = [Layer.Engine, Layer.EngineSupport, Layer.Common, Layer.Protocol, Layer.Gateway,
                        Layer.Transport, Layer.Showcase],
    };

    private static readonly Dictionary<Layer, string> RuleText = new()
    {
        [Layer.Engine] = "rule 1: the engine references nothing but the BCL",
        [Layer.EngineSupport] = "engine support projects reference only the engine",
        [Layer.Common] = "rule 3: Common references only the engine",
        [Layer.Protocol] = "rule 2: protocol projects reference nothing, not even the engine",
        [Layer.Gateway] = "rule 4: a gateway references the engine, Common and protocols only - never a transport, a host or another gateway",
        [Layer.Transport] = "rule 5: a transport references the engine and one protocol - never a gateway",
        [Layer.Showcase] = "the showcase references gateways, transports, the engine and its in-memory support - never a host",
        [Layer.Host] = "rule 6: a host may reference every layer except another host",
    };

    /// <summary>Layers whose projects may not use any NuGet package (rules 1 and 2: BCL only).</summary>
    public static bool IsBclOnly(Layer layer) => layer is Layer.Engine or Layer.Protocol;

    /// <summary>The layer of a project, from its folder under <c>src/</c>; null for unknown folders.</summary>
    public static Layer? LayerOf(ProjectInfo project) => LayerOf(project.Folder, project.Name);

    public static Layer? LayerOf(string folder, string name) => folder switch
    {
        "Engine" => name == EngineProject ? Layer.Engine : Layer.EngineSupport,
        "Common" => Layer.Common,
        "Protocols" => Layer.Protocol,
        "Gateways" => Layer.Gateway,
        "Transports" => Layer.Transport,
        "Showcase" => Layer.Showcase,
        "Hosts" => Layer.Host,
        _ => null,
    };

    /// <summary>The project-name prefix every project in a layer folder must have.</summary>
    public static string NamePrefixOf(Layer layer) => layer switch
    {
        Layer.Engine => EngineProject,
        Layer.EngineSupport => EngineProject + ".",
        Layer.Common => "SimNetGateway.Common",
        Layer.Protocol => "SimNetGateway.Protocol.",
        Layer.Gateway => "SimNetGateway.Gateway.",
        Layer.Transport => "SimNetGateway.Transport.",
        Layer.Showcase => "SimNetGateway.Showcase",
        _ => "SimNetGateway.",
    };

    /// <summary>
    /// Every violation of the dependency rules by one project; empty when the project complies.
    /// </summary>
    public static List<string> Check(
        string project, Layer layer, IReadOnlyList<Reference> references, IReadOnlyList<string> packages)
    {
        var violations = new List<string>();
        var allowed = Allowed[layer];

        foreach (var reference in references)
        {
            if (reference.Layer is not { } target)
            {
                violations.Add($"{project} references {reference.Name}, which is not a src project of a known layer");
            }
            else if (!allowed.Contains(target))
            {
                violations.Add($"{project} ({layer}) references {reference.Name} ({target}) - {RuleText[layer]}");
            }
        }

        if (layer == Layer.Transport)
        {
            var protocols = references.Where(r => r.Layer == Layer.Protocol).Select(r => r.Name).ToList();
            if (protocols.Count > 1)
            {
                violations.Add($"{project} references {protocols.Count} protocols ({string.Join(", ", protocols)}) - {RuleText[layer]}");
            }
        }

        if (IsBclOnly(layer))
        {
            foreach (var package in packages)
            {
                violations.Add($"{project} ({layer}) references package {package} - {RuleText[layer]}");
            }
        }

        return violations;
    }
}
