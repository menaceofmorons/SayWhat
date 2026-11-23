using System.Text.RegularExpressions;
namespace SayWhat.Tests;

public class SolutionIntegrationTests
{
    private static string SrcDirectory => RepoPaths.SrcDirectory;

    private static readonly string[] ExpectedProjects =
    [
        "SayWhat.Core",
        "SayWhat.Engines",
        "SayWhat.Platform.Windows",
        "SayWhat.Spike",
        "SayWhat.Tests",
    ];

    private static readonly Dictionary<string, string> ClearedPackages = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Microsoft.NET.Test.Sdk"] = "18.12.0",
        ["xunit.v3"] = "4.0.3",
        ["xunit.runner.visualstudio"] = "4.0.1",
        ["Microsoft.ML.Tokenizers"] = "3.0.0",
    };

    private static readonly string[] RejectedPackagePrefixes =
    [
        "FluentAssertions",
        "Microsoft.AI.DirectML",
        "Microsoft.ML.OnnxRuntime.DirectML",
    ];

    [Fact]
    public void Solution_ListsExactlyTheFiveProjects()
    {
        var text = File.ReadAllText(Path.Combine(SrcDirectory, "SayWhat.sln"));
        var projects = Regex.Matches(text, @"^Project\(""[^""]+""\) = ""([^""]+)""", RegexOptions.Multiline)
            .Select(match => match.Groups[1].Value)
            .Order(StringComparer.Ordinal)
            .ToArray();

        Assert.Equal(ExpectedProjects, projects);
    }

    [Fact]
    public void EveryProjectFile_IsInTheSolution()
    {
        var onDisk = Directory.GetFiles(SrcDirectory, "*.csproj", SearchOption.AllDirectories)
            .Select(Path.GetFileNameWithoutExtension)
            .Order(StringComparer.Ordinal)
            .ToArray();

        Assert.Equal(ExpectedProjects, onDisk);
    }

    [Fact]
    public void ThirdPartyPackages_AreExactlyTheClearedPackagesAtTheClearedVersions()
    {
        var used = AllPackageReferences()
            .ToDictionary(reference => reference.Name, reference => reference.Version, StringComparer.OrdinalIgnoreCase);

        Assert.Equal(
            ClearedPackages.OrderBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase),
            used.OrderBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase));
    }

    [Fact]
    public void NoProject_ReferencesAnUnclearedPackage()
    {
        var names = AllPackageReferences().Select(reference => reference.Name).ToArray();

        foreach (var prefix in RejectedPackagePrefixes)
        {
            Assert.DoesNotContain(names, name => name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase));
        }
    }

    [Fact]
    public void PackageVersions_AreDeclaredInTheProjectFilesOnly()
    {
        var central = Directory.GetFiles(SrcDirectory, "Directory.Packages.props", SearchOption.AllDirectories);
        var rootCentral = Path.Combine(Path.GetDirectoryName(SrcDirectory)!, "Directory.Packages.props");

        Assert.Empty(central);
        Assert.False(File.Exists(rootCentral));
    }

    private static IEnumerable<(string Name, string Version)> AllPackageReferences() =>
        ExpectedProjects.SelectMany(project =>
            RepoPaths.LoadProject(project)
                .Descendants("PackageReference")
                .Select(e => ((string)e.Attribute("Include")!, (string?)e.Attribute("Version") ?? string.Empty)));
}
