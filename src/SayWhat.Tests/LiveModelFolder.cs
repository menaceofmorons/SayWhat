using SayWhat.Engines.Lite;

namespace SayWhat.Tests;

public sealed class LiveModelFolder : IDisposable
{
    private LiveModelFolder(string root)
    {
        Root = root;
    }

    public string Root { get; }

    public static async Task<LiveModelFolder?> CreateAsync(string prefix, string[] pairs)
    {
        var folder = new LiveModelFolder(Path.Combine(Path.GetTempPath(), prefix + Guid.NewGuid().ToString("N")));
        try
        {
            foreach (var pair in pairs)
            {
                var files = await ModelTestCache.TryGetAllAsync(pair, LiteEngine.RequiredFiles.ToArray());
                if (files is null)
                {
                    folder.Dispose();
                    return null;
                }

                foreach (var (name, path) in files)
                {
                    var target = Path.Combine(folder.Root, "lite", pair, name);
                    Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                    File.Copy(path, target);
                }
            }
        }
        catch
        {
            folder.Dispose();
            throw;
        }

        return folder;
    }

    public void Dispose()
    {
        if (Directory.Exists(Root))
        {
            Directory.Delete(Root, recursive: true);
        }
    }
}
