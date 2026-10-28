using System.Reflection;

namespace SimNetGateway.Architecture.Tests;

/// <summary>
/// Enforces the dependency rules of DD-003 section 3 (R-049, R-050) on the real projects, twice:
/// on the declared project references (which fail even while a reference is still unused) and on
/// the compiled assemblies' metadata references (which also catch a reference added another way).
/// </summary>
public class DependencyRuleTests
{
    public static TheoryData<string> SrcProjectNames()
    {
        var data = new TheoryData<string>();
        foreach (var project in SourceTree.SrcProjects)
        {
            data.Add(project.Name);
        }
        return data;
    }

    [Fact]
    public void Source_tree_contains_the_projects_of_DD003()
    {
        string[] expected =
        [
            "SimNetGateway.Engine", "SimNetGateway.Engine.InMemory", "SimNetGateway.Engine.Monitoring", "SimNetGateway.Common",
            "SimNetGateway.Protocol.Ned", "SimNetGateway.Protocol.Rpr", "SimNetGateway.Protocol.Display", "SimNetGateway.Protocol.Dis7",
            "SimNetGateway.Transport.FakeRti", "SimNetGateway.Gateway.NedRpr", "SimNetGateway.Showcase", "SimNetGateway.Demo", "SimNetGateway.Viewer",
        ];
        var actual = SourceTree.SrcProjects.Select(p => p.Name).ToList();
        Assert.All(expected, name => Assert.Contains(name, actual));
    }

    [Theory]
    [MemberData(nameof(SrcProjectNames))]
    public void Project_lives_in_a_known_layer_folder(string name)
    {
        var project = SourceTree.FindSrc(name)!;
        Assert.True(LayerRules.LayerOf(project) is not null,
            $"{project.RelativePath}: folder src/{project.Folder} is not a layer of DD-003 section 2");
    }

    [Theory]
    [MemberData(nameof(SrcProjectNames))]
    public void Declared_project_references_follow_the_rules(string name)
    {
        var project = SourceTree.FindSrc(name)!;
        var layer = LayerRules.LayerOf(project)!.Value;
        var references = project.ProjectReferences.Select(ToReference).ToList();

        var violations = LayerRules.Check(project.Name, layer, references, project.PackageReferences);

        Assert.True(violations.Count == 0, string.Join(Environment.NewLine, violations));
    }

    [Theory]
    [MemberData(nameof(SrcProjectNames))]
    public void Compiled_assembly_references_follow_the_rules(string name)
    {
        var project = SourceTree.FindSrc(name)!;
        var layer = LayerRules.LayerOf(project)!.Value;
        var assembly = Assembly.Load(new AssemblyName(project.Name));
        var references = assembly.GetReferencedAssemblies()
            .Select(a => a.Name!)
            .Where(n => n.StartsWith("SimNetGateway.", StringComparison.Ordinal))
            .Select(ToReference)
            .ToList();

        var violations = LayerRules.Check(project.Name, layer, references, []);

        Assert.True(violations.Count == 0, string.Join(Environment.NewLine, violations));
    }

    [Theory]
    [MemberData(nameof(SrcProjectNames))]
    public void Bcl_only_layers_reference_only_the_shared_framework(string name)
    {
        var project = SourceTree.FindSrc(name)!;
        if (!LayerRules.IsBclOnly(LayerRules.LayerOf(project)!.Value))
        {
            return;
        }

        // The BCL is the shared framework the runtime itself was loaded from.
        var frameworkDir = Path.GetDirectoryName(typeof(object).Assembly.Location)!;
        var assembly = Assembly.Load(new AssemblyName(project.Name));
        var foreign = assembly.GetReferencedAssemblies()
            .Select(a => a.Name!)
            .Where(n => !File.Exists(Path.Join(frameworkDir, n + ".dll")))
            .ToList();

        Assert.True(foreign.Count == 0, $"{project.Name} references non-BCL assemblies: {string.Join(", ", foreign)}");
    }

    private static Reference ToReference(string name)
    {
        var target = SourceTree.FindSrc(name);
        return new Reference(name, target is null ? null : LayerRules.LayerOf(target));
    }
}
