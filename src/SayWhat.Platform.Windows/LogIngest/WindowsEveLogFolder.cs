using SayWhat.Core.LogIngest;

namespace SayWhat.Platform.Windows.LogIngest;

public sealed class WindowsEveLogFolder : IEveLogFolder
{
    private readonly Func<string> _documentsFolder;

    public WindowsEveLogFolder()
        : this(() => Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments))
    {
    }

    public WindowsEveLogFolder(Func<string> documentsFolder)
    {
        _documentsFolder = documentsFolder;
    }

    public string Path => System.IO.Path.Combine(_documentsFolder(), "EVE", "logs", "Chatlogs");
}
