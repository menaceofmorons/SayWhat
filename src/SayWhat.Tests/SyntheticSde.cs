using System.IO.Compression;
using System.Text;
using SayWhat.Core.ReferenceData;

namespace SayWhat.Tests;

internal sealed class SyntheticSde : ISdeSource
{
    public static readonly string[] Languages = ["de", "en", "es", "fr", "ja", "ko", "ru", "zh"];

    private readonly Dictionary<string, Func<Stream>> tables = new();

    public static SyntheticSde Create(int ships = 3, int systems = 3, bool gzip = false)
    {
        var sde = new SyntheticSde();
        sde.Set("_sde", gzip, "{\"key\":\"sde\",\"buildNumber\":4242,\"releaseDate\":\"2026-11-20T16:58:26Z\"}");
        sde.Set("translationLanguages", gzip, Languages.Select(code => $"{{\"language\":\"{code}\",\"name\":\"{code}\"}}").ToArray());
        sde.Set("groups", gzip, "{\"groupID\":25,\"categoryID\":6,\"name\":{\"en\":\"Frigate\"}}", "{\"groupID\":99,\"categoryID\":7,\"name\":{\"en\":\"Module\"}}");
        var types = new List<string> { "{\"typeID\":9001,\"groupID\":99,\"name\":" + NameMap(id => $"Module {id}") + "}" };
        for (var i = 0; i < ships; i++)
        {
            types.Add(ShipLine(600 + i, 25, code => code == "en" ? $"Ship{i}" : $"Ship{i}-{code}"));
        }

        sde.Set("types", gzip, types.ToArray());
        sde.Set("mapSolarSystems", gzip, Enumerable.Range(0, systems).Select(i => $"{{\"solarSystemID\":{30000000 + i},\"name\":{NameMap(code => code == "en" ? $"Sys{i}" : $"Sys{i}-{code}")}}}").ToArray());
        return sde;
    }

    public static string ShipLine(int typeId, int groupId, Func<string, string> name) =>
        $"{{\"typeID\":{typeId},\"groupID\":{groupId},\"name\":{NameMap(name)}}}";

    public static string NameMap(Func<string, string> name) =>
        "{" + string.Join(",", Languages.Select(code => $"\"{code}\":\"{name(code)}\"")) + "}";

    public void Set(string table, bool gzip, params string[] lines)
    {
        var bytes = Encoding.UTF8.GetBytes(string.Join("\n", lines) + "\n");
        if (gzip)
        {
            using var buffer = new MemoryStream();
            using (var zip = new GZipStream(buffer, CompressionLevel.Fastest, leaveOpen: true))
            {
                zip.Write(bytes);
            }

            bytes = buffer.ToArray();
        }

        tables[table] = () => new MemoryStream(bytes);
    }

    public void SetStream(string table, Func<Stream> open) => tables[table] = open;

    public Stream Open(string table) => tables[table]();
}
