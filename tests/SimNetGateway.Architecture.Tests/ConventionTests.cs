namespace SimNetGateway.Architecture.Tests;

/// <summary>
/// Structural conventions of DD-003 section 2: folder name = project name = root namespace,
/// layer-specific name prefixes, central package versions, one test project per layer, and every
/// project in the solution.
/// </summary>
public class ConventionTests
{
    private static IEnumerable<ProjectInfo> AllProjects => SourceTree.SrcProjects.Concat(SourceTree.TestProjects);

    [Fact]
    public void Folder_name_equals_project_name()
    {
        var wrong = AllProjects.Where(p => p.DirectoryName != p.Name).Select(p => p.RelativePath).ToList();

        Assert.True(wrong.Count == 0, "folder name differs from project name: " + string.Join(", ", wrong));
    }

    [Fact]
    public void Assembly_name_and_root_namespace_are_not_overridden()
    {
        var wrong = AllProjects
            .Where(p => (p.Properties.TryGetValue("AssemblyName", out var a) && a != p.Name)
                     || (p.Properties.TryGetValue("RootNamespace", out var n) && n != p.Name))
            .Select(p => p.RelativePath)
            .ToList();

        Assert.True(wrong.Count == 0, "assembly name or root namespace differs from project name: " + string.Join(", ", wrong));
    }

    [Fact]
    public void Target_framework_comes_from_Directory_Build_props()
    {
        var wrong = AllProjects
            .Where(p => p.Properties.ContainsKey("TargetFramework") || p.Properties.ContainsKey("TargetFrameworks"))
            .Select(p => p.RelativePath)
            .ToList();

        Assert.True(wrong.Count == 0, "projects override the target framework (R-001): " + string.Join(", ", wrong));
    }

    [Fact]
    public void Project_name_matches_its_layer_prefix()
    {
        var wrong = SourceTree.SrcProjects
            .Where(p => LayerRules.LayerOf(p) is { } layer && !p.Name.StartsWith(LayerRules.NamePrefixOf(layer), StringComparison.Ordinal))
            .Select(p => p.RelativePath)
            .ToList();

        Assert.True(wrong.Count == 0, "project name does not match its layer folder: " + string.Join(", ", wrong));
    }

    [Fact]
    public void Package_versions_live_only_in_Directory_Packages_props()
    {
        var wrong = new List<string>();
        foreach (var project in AllProjects)
        {
            var text = File.ReadAllText(Path.Join(SourceTree.Root, project.RelativePath));
            if (text.Contains("<PackageReference", StringComparison.Ordinal) && text.Contains("Version=", StringComparison.Ordinal))
            {
                wrong.Add(project.RelativePath);
            }
        }

        Assert.True(File.Exists(Path.Join(SourceTree.Root, "Directory.Packages.props")));
        Assert.True(wrong.Count == 0, "package versions outside Directory.Packages.props (R-053): " + string.Join(", ", wrong));
    }

    [Fact]
    public void Each_test_project_mirrors_one_src_project_and_references_it()
    {
        var wrong = new List<string>();
        foreach (var test in SourceTree.TestProjects)
        {
            if (test.Name == "SimNetGateway.Architecture.Tests")
            {
                continue;
            }
            if (!test.Name.EndsWith(".Tests", StringComparison.Ordinal))
            {
                wrong.Add($"{test.Name}: test project names end with .Tests");
                continue;
            }
            var subject = test.Name[..^".Tests".Length];
            if (SourceTree.FindSrc(subject) is null)
            {
                wrong.Add($"{test.Name}: no src project {subject}");
            }
            else if (!test.ProjectReferences.Contains(subject))
            {
                wrong.Add($"{test.Name}: does not reference {subject}");
            }
        }

        Assert.True(wrong.Count == 0, string.Join(Environment.NewLine, wrong));
    }

    [Fact]
    public void Every_project_is_in_the_solution()
    {
        var solution = File.ReadAllLines(Path.Join(SourceTree.Root, SourceTree.SolutionFile))
            .Where(line => line.StartsWith("Project(", StringComparison.Ordinal))
            .SelectMany(line => line.Split('"'))
            .Where(part => part.EndsWith(".csproj", StringComparison.Ordinal))
            // the .sln format always writes '\'; compare with '/' on every OS
            .Select(part => part.Replace('\\', '/'))
            .ToHashSet(StringComparer.Ordinal);

        var missing = AllProjects.Select(p => p.RelativePath).Where(p => !solution.Contains(p)).ToList();

        Assert.True(missing.Count == 0, "projects missing from the solution: " + string.Join(", ", missing));
    }
}
