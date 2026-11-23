namespace SayWhat.Tests;

internal static class ModelTestCache
{
    private const string FetchVariable = "SAYWHAT_FETCH_TEST_MODELS";
    private const string RepositoryUrl = "https://huggingface.co/Xenova/opus-mt-{0}/resolve/main/{1}";

    private static readonly HttpClient Client = new();

    public static string Root { get; } = Path.Combine(RepoPaths.SrcDirectory, "SayWhat.Tests", ".model-cache");

    public static string PathFor(string pair, string file) => Path.Combine(Root, pair, file);

    public static async Task<string?> TryGetAsync(string pair, string file)
    {
        var path = PathFor(pair, file);
        if (File.Exists(path))
        {
            return path;
        }

        if (Environment.GetEnvironmentVariable(FetchVariable) != "1")
        {
            return null;
        }

        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var partial = path + ".partial";
        await using (var source = await Client.GetStreamAsync(string.Format(RepositoryUrl, pair, file)))
        await using (var target = File.Create(partial))
        {
            await source.CopyToAsync(target);
        }

        File.Move(partial, path, overwrite: true);
        return path;
    }

    public static async Task<IReadOnlyDictionary<string, string>?> TryGetAllAsync(string pair, params string[] files)
    {
        var paths = new Dictionary<string, string>();
        foreach (var file in files)
        {
            var path = await TryGetAsync(pair, file);
            if (path is null)
            {
                return null;
            }

            paths[file] = path;
        }

        return paths;
    }
}
