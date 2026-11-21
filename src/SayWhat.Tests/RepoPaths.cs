using System.Xml.Linq;

namespace SayWhat.Tests;

internal static class RepoPaths
{
    public static string SrcDirectory { get; } = FindSrcDirectory();

    public static XDocument LoadProject(string project) =>
        XDocument.Load(Path.Combine(SrcDirectory, project, project + ".csproj"));

    private static string FindSrcDirectory()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "SayWhat.sln")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName ?? throw new InvalidOperationException("SayWhat.sln not found");
    }
}
