using SayWhat.Platform.Windows.LogIngest;

namespace SayWhat.Tests.LogIngest;

public class EveLogFolderTests
{
    [Fact]
    public void Path_IsDocumentsPlusEveLogsChatlogs()
    {
        var documents = Path.Combine(Path.GetTempPath(), "docs");
        var folder = new WindowsEveLogFolder(() => documents);

        Assert.Equal(Path.Combine(documents, "EVE", "logs", "Chatlogs"), folder.Path);
    }

    [Fact]
    public void DefaultConstructor_UsesKnownDocumentsFolder()
    {
        var expected = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "EVE", "logs", "Chatlogs");

        Assert.Equal(expected, new WindowsEveLogFolder().Path);
    }

    [Fact]
    public void DifferentDocumentsFolder_GivesDifferentPath()
    {
        var a = new WindowsEveLogFolder(() => Path.Combine(Path.GetTempPath(), "a"));
        var b = new WindowsEveLogFolder(() => Path.Combine(Path.GetTempPath(), "b"));

        Assert.NotEqual(a.Path, b.Path);
    }
}
