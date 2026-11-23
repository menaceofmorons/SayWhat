namespace SayWhat.Tests;

internal static class SdeTestCache
{
    private const string FetchVariable = "SAYWHAT_FETCH_TEST_SDE";
    private const string SourceUrl = "https://sde.evetech.net/tranquility/latest/{0}.jsonl";

    private static readonly string[] Tables = ["_sde", "groups", "types", "mapSolarSystems", "translationLanguages"];
    private static readonly HttpClient Client = new() { Timeout = TimeSpan.FromMinutes(10) };

    public static string Root { get; } = Path.Combine(RepoPaths.SrcDirectory, "SayWhat.Tests", ".sde-cache");

    public static async Task<string?> TryGetAsync()
    {
        if (Tables.All(table => File.Exists(PathFor(table))))
        {
            return Root;
        }

        if (Environment.GetEnvironmentVariable(FetchVariable) != "1")
        {
            return null;
        }

        Directory.CreateDirectory(Root);
        foreach (var table in Tables.Where(table => !File.Exists(PathFor(table))))
        {
            var partial = PathFor(table) + ".partial";
            await using (var source = await Client.GetStreamAsync(string.Format(SourceUrl, table)))
            await using (var target = File.Create(partial))
            {
                await source.CopyToAsync(target);
            }

            File.Move(partial, PathFor(table), overwrite: true);
        }

        return Root;
    }

    private static string PathFor(string table) => Path.Combine(Root, table + ".jsonl");
}
