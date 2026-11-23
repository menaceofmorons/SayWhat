namespace SayWhat.Tests;

public class LayerDependencyTests
{
    [Fact]
    public void Core_ReferencesNoOtherProject()
    {
        Assert.Empty(ProjectReferences("SayWhat.Core"));
    }

    [Theory]
    [InlineData("SayWhat.Engines")]
    [InlineData("SayWhat.Platform.Windows")]
    public void Layer_ReferencesOnlyCore(string project)
    {
        Assert.Equal(["SayWhat.Core"], ProjectReferences(project));
    }

    [Fact]
    public void Spike_ReferencesCoreEnginesAndPlatform()
    {
        Assert.Equal(
            ["SayWhat.Core", "SayWhat.Engines", "SayWhat.Platform.Windows"],
            ProjectReferences("SayWhat.Spike"));
    }

    [Theory]
    [InlineData("SayWhat.Core")]
    [InlineData("SayWhat.Engines")]
    [InlineData("SayWhat.Platform.Windows")]
    [InlineData("SayWhat.Spike")]
    public void NoSourceProject_ReferencesSpike(string project)
    {
        Assert.DoesNotContain("SayWhat.Spike", ProjectReferences(project));
    }

    [Fact]
    public void Tests_ReferenceAllFourSourceProjects()
    {
        Assert.Equal(
            ["SayWhat.Core", "SayWhat.Engines", "SayWhat.Platform.Windows", "SayWhat.Spike"],
            ProjectReferences("SayWhat.Tests"));
    }

    [Theory]
    [InlineData("SayWhat.Core")]
    [InlineData("SayWhat.Engines")]
    [InlineData("SayWhat.Spike")]
    public void SourceProject_HasNoPackageReference(string project)
    {
        Assert.Empty(PackageReferences(project));
    }

    [Fact]
    public void Platform_UsesOnlyTheClearedFastTextBinding()
    {
        Assert.Equal(
            ["Panlingo.LanguageIdentification.FastText", "Panlingo.LanguageIdentification.FastText.Native"],
            PackageReferences("SayWhat.Platform.Windows"));
    }

    [Fact]
    public void Tests_UseOnlyTheClearedTestFramework()
    {
        Assert.Equal(
            ["Microsoft.NET.Test.Sdk", "xunit.runner.visualstudio", "xunit.v3"],
            PackageReferences("SayWhat.Tests"));
    }

    private static string[] ProjectReferences(string project) =>
        Items(project, "ProjectReference")
            .Select(path => Path.GetFileNameWithoutExtension(path.Replace('\\', '/')))
            .Order(StringComparer.Ordinal)
            .ToArray();

    private static string[] PackageReferences(string project) =>
        Items(project, "PackageReference").Order(StringComparer.OrdinalIgnoreCase).ToArray();

    private static IEnumerable<string> Items(string project, string element) =>
        RepoPaths.LoadProject(project)
            .Descendants(element)
            .Select(e => (string)e.Attribute("Include")!);
}
