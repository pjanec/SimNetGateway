using System.Xml.Linq;

namespace SimNetGateway.Architecture.Tests;

/// <summary>One project file of the repository, as read from the source tree.</summary>
/// <param name="Name">Project name = file name without extension = assembly name (DD-003 conventions).</param>
/// <param name="RelativePath">Path relative to the repository root, with '/' separators.</param>
/// <param name="Folder">Layer folder under <c>src/</c> (<c>Engine</c>, <c>Protocols</c>, ...), or <c>tests</c>.</param>
/// <param name="DirectoryName">Name of the directory that holds the project file.</param>
/// <param name="ProjectReferences">Names of the referenced projects.</param>
/// <param name="PackageReferences">Ids of the referenced NuGet packages.</param>
/// <param name="Properties">MSBuild properties that must follow the conventions when set explicitly.</param>
public sealed record ProjectInfo(
    string Name,
    string RelativePath,
    string Folder,
    string DirectoryName,
    IReadOnlyList<string> ProjectReferences,
    IReadOnlyList<string> PackageReferences,
    IReadOnlyDictionary<string, string> Properties)
{
    public override string ToString() => Name;
}

/// <summary>Reads the project files of the repository the tests were built from.</summary>
public static class SourceTree
{
    public const string SolutionFile = "SimNetGateway.sln";

    public static string Root { get; } = FindRoot();

    /// <summary>Projects under <c>src/</c>, sorted by name.</summary>
    public static IReadOnlyList<ProjectInfo> SrcProjects { get; } = Load("src");

    /// <summary>Projects under <c>tests/</c>, sorted by name.</summary>
    public static IReadOnlyList<ProjectInfo> TestProjects { get; } = Load("tests");

    public static ProjectInfo? FindSrc(string name) => SrcProjects.FirstOrDefault(p => p.Name == name);

    private static string FindRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Join(dir.FullName, SolutionFile)))
        {
            dir = dir.Parent;
        }
        return dir?.FullName
            ?? throw new InvalidOperationException($"{SolutionFile} not found above {AppContext.BaseDirectory}");
    }

    private static IReadOnlyList<ProjectInfo> Load(string top)
    {
        var topDir = Path.Join(Root, top);
        return Directory.EnumerateFiles(topDir, "*.csproj", SearchOption.AllDirectories)
            .Where(path => !IsBuildOutput(Path.GetRelativePath(topDir, path)))
            .Select(path => Read(path, top))
            .OrderBy(p => p.Name, StringComparer.Ordinal)
            .ToList();
    }

    private static bool IsBuildOutput(string relativePath)
    {
        var parts = relativePath.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        return parts.Contains("bin") || parts.Contains("obj");
    }

    private static ProjectInfo Read(string path, string top)
    {
        var doc = XDocument.Load(path);
        var projectDir = Path.GetDirectoryName(path)!;

        var projectRefs = doc.Descendants()
            .Where(e => e.Name.LocalName == "ProjectReference")
            .Select(e => (string?)e.Attribute("Include") ?? "")
            // MSBuild accepts both separators; normalise so the name is found on every OS (R-012).
            .Select(include => include.Replace('\\', '/'))
            .Select(include => Path.GetFileNameWithoutExtension(Path.GetFullPath(Path.Join(projectDir, include))))
            .ToList();

        var packageRefs = doc.Descendants()
            .Where(e => e.Name.LocalName == "PackageReference")
            .Select(e => (string?)e.Attribute("Include") ?? "")
            .ToList();

        var properties = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var name in new[] { "AssemblyName", "RootNamespace", "TargetFramework", "TargetFrameworks" })
        {
            var element = doc.Descendants().FirstOrDefault(e => e.Name.LocalName == name);
            if (element is not null)
            {
                properties[name] = element.Value.Trim();
            }
        }

        var relative = Path.GetRelativePath(Root, path).Replace('\\', '/');
        var segments = relative.Split('/');
        var folder = top == "src" ? segments[1] : top;

        return new ProjectInfo(
            Name: Path.GetFileNameWithoutExtension(path),
            RelativePath: relative,
            Folder: folder,
            DirectoryName: Path.GetFileName(projectDir),
            ProjectReferences: projectRefs,
            PackageReferences: packageRefs,
            Properties: properties);
    }
}
