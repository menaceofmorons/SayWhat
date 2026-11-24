using System.Globalization;
using System.IO.Compression;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace SayWhat.Core.ReferenceData;

public sealed class SdeImporter
{
    private const int ShipCategoryId = 6;
    private const string PendingSuffix = ".new";

    private readonly ISdeSource source;
    private readonly SdeImportOptions options;

    public SdeImporter(ISdeSource source, SdeImportOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(source);
        this.source = source;
        this.options = options ?? new SdeImportOptions();
    }

    public SdeImportResult Import(string databasePath, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(databasePath);
        var pendingPath = databasePath + PendingSuffix;
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(databasePath))!);
        DeleteIfPresent(pendingPath);

        try
        {
            var result = Build(pendingPath, cancellationToken);
            FlushToDisk(pendingPath);
            File.Move(pendingPath, databasePath, overwrite: true);
            return result;
        }
        catch (Exception exception)
        {
            DeleteIfPresent(pendingPath);
            if (exception is SdeImportException or OperationCanceledException)
            {
                throw;
            }

            throw new SdeImportException("The SDE import failed; the previous reference database is kept.", exception);
        }
    }

    private SdeImportResult Build(string pendingPath, CancellationToken cancellationToken)
    {
        var version = ReadVersion();
        var languages = ReadLanguages();
        var shipGroups = ReadShipGroups(cancellationToken);

        int shipTypes;
        int shipNames;
        int systems;
        int systemNames;
        using (var connection = ReferenceSchema.Open(pendingPath, SqliteOpenMode.ReadWriteCreate))
        {
            Execute(connection, "PRAGMA journal_mode = OFF");
            Execute(connection, "PRAGMA synchronous = OFF");
            ReferenceSchema.Create(connection);

            using var transaction = connection.BeginTransaction();
            (shipTypes, shipNames) = ImportShips(connection, transaction, shipGroups, languages, cancellationToken);
            (systems, systemNames) = ImportSystems(connection, transaction, cancellationToken);
            InsertBuild(connection, transaction, version);
            transaction.Commit();

            Verify(connection, languages, shipTypes, systems);
        }

        return new SdeImportResult(version, shipTypes, shipNames, systems, systemNames);
    }

    private string ReadVersion()
    {
        foreach (var document in ReadRecords(SdeTables.Build, CancellationToken.None))
        {
            using (document)
            {
                if (document.RootElement.TryGetProperty("buildNumber", out var build))
                {
                    return build.ToString();
                }
            }
        }

        throw new SdeImportException("The SDE build record has no build number.");
    }

    private List<string> ReadLanguages()
    {
        var languages = new List<string>();
        foreach (var document in ReadRecords(SdeTables.Languages, CancellationToken.None))
        {
            using (document)
            {
                if (document.RootElement.TryGetProperty("language", out var language) && language.GetString() is { Length: > 0 } code)
                {
                    languages.Add(code);
                }
            }
        }

        if (languages.Count == 0)
        {
            throw new SdeImportException("The SDE lists no translation languages.");
        }

        return languages;
    }

    private HashSet<long> ReadShipGroups(CancellationToken cancellationToken)
    {
        var groups = new HashSet<long>();
        foreach (var document in ReadRecords(SdeTables.Groups, cancellationToken))
        {
            using (document)
            {
                var root = document.RootElement;
                if (root.TryGetProperty("categoryID", out var category)
                    && category.TryGetInt32(out var categoryId)
                    && categoryId == ShipCategoryId
                    && root.TryGetProperty("groupID", out var group))
                {
                    groups.Add(group.GetInt64());
                }
            }
        }

        return groups;
    }

    private (int Types, int Names) ImportShips(
        SqliteConnection connection,
        SqliteTransaction transaction,
        HashSet<long> shipGroups,
        List<string> languages,
        CancellationToken cancellationToken)
    {
        using var insert = connection.CreateCommand();
        insert.Transaction = transaction;
        insert.CommandText = "INSERT OR REPLACE INTO ship_name (type_id, language, name, normalised_name) VALUES ($type, $language, $name, $normalised)";
        var type = insert.Parameters.Add("$type", SqliteType.Integer);
        var language = insert.Parameters.Add("$language", SqliteType.Text);
        var name = insert.Parameters.Add("$name", SqliteType.Text);
        var normalised = insert.Parameters.Add("$normalised", SqliteType.Text);

        var types = 0;
        var names = 0;
        foreach (var document in ReadRecords(SdeTables.Types, cancellationToken))
        {
            using (document)
            {
                var root = document.RootElement;
                if (!root.TryGetProperty("groupID", out var group)
                    || !shipGroups.Contains(group.GetInt64())
                    || !root.TryGetProperty("typeID", out var typeId)
                    || !root.TryGetProperty("name", out var nameMap)
                    || nameMap.ValueKind != JsonValueKind.Object)
                {
                    continue;
                }

                var rowsForType = 0;
                foreach (var code in languages)
                {
                    if (nameMap.TryGetProperty(code, out var value) && value.GetString() is { Length: > 0 } text)
                    {
                        type.Value = typeId.GetInt32();
                        language.Value = code;
                        name.Value = text;
                        normalised.Value = NameNormaliser.Normalise(text);
                        insert.ExecuteNonQuery();
                        rowsForType++;
                    }
                }

                if (rowsForType > 0)
                {
                    types++;
                    names += rowsForType;
                }
            }
        }

        return (types, names);
    }

    private (int Systems, int Names) ImportSystems(
        SqliteConnection connection,
        SqliteTransaction transaction,
        CancellationToken cancellationToken)
    {
        using var insert = connection.CreateCommand();
        insert.Transaction = transaction;
        insert.CommandText = "INSERT OR REPLACE INTO system_name (system_id, language, name) VALUES ($system, $language, $name)";
        var system = insert.Parameters.Add("$system", SqliteType.Integer);
        var language = insert.Parameters.Add("$language", SqliteType.Text);
        var name = insert.Parameters.Add("$name", SqliteType.Text);

        var systems = 0;
        var names = 0;
        foreach (var document in ReadRecords(SdeTables.SolarSystems, cancellationToken))
        {
            using (document)
            {
                var root = document.RootElement;
                if (!root.TryGetProperty("solarSystemID", out var id)
                    || !root.TryGetProperty("name", out var nameMap)
                    || nameMap.ValueKind != JsonValueKind.Object)
                {
                    continue;
                }

                systems++;
                foreach (var entry in nameMap.EnumerateObject())
                {
                    if (entry.Value.GetString() is { Length: > 0 } text)
                    {
                        system.Value = id.GetInt32();
                        language.Value = entry.Name;
                        name.Value = text;
                        insert.ExecuteNonQuery();
                        names++;
                    }
                }
            }
        }

        return (systems, names);
    }

    private void InsertBuild(SqliteConnection connection, SqliteTransaction transaction, string version)
    {
        using var insert = connection.CreateCommand();
        insert.Transaction = transaction;
        insert.CommandText = "INSERT INTO sde_build (sde_version, imported_at) VALUES ($version, $at)";
        insert.Parameters.AddWithValue("$version", version);
        insert.Parameters.AddWithValue("$at", options.Clock.GetUtcNow().ToString("O", CultureInfo.InvariantCulture));
        insert.ExecuteNonQuery();
    }

    private void Verify(SqliteConnection connection, List<string> languages, int shipTypes, int systems)
    {
        if (shipTypes < options.MinimumShipTypes)
        {
            throw new SdeImportException($"Only {shipTypes} ship types were imported; at least {options.MinimumShipTypes} are required.");
        }

        if (systems < options.MinimumSolarSystems)
        {
            throw new SdeImportException($"Only {systems} solar systems were imported; at least {options.MinimumSolarSystems} are required.");
        }

        using var command = connection.CreateCommand();
        command.CommandText = "SELECT DISTINCT language FROM ship_name";
        var present = new HashSet<string>(StringComparer.Ordinal);
        using (var reader = command.ExecuteReader())
        {
            while (reader.Read())
            {
                present.Add(reader.GetString(0));
            }
        }

        var missing = languages.Where(code => !present.Contains(code)).ToArray();
        if (missing.Length > 0)
        {
            throw new SdeImportException($"Ship names are missing for languages: {string.Join(", ", missing)}.");
        }
    }

    private IEnumerable<JsonDocument> ReadRecords(string table, CancellationToken cancellationToken)
    {
        using var raw = source.Open(table);
        var magic = new byte[2];
        var read = 0;
        while (read < magic.Length)
        {
            var count = raw.Read(magic, read, magic.Length - read);
            if (count == 0)
            {
                break;
            }

            read += count;
        }

        var head = magic[..read];
        using var prefixed = new PrefixedStream(head, raw);
        var isGzip = read == 2 && magic[0] == 0x1f && magic[1] == 0x8b;
        using var stream = isGzip ? new GZipStream(prefixed, CompressionMode.Decompress) : (Stream)prefixed;
        using var reader = new StreamReader(stream, Encoding.UTF8);
        while (reader.ReadLine() is { } line)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (line.Length == 0)
            {
                continue;
            }

            yield return JsonDocument.Parse(line);
        }
    }

    private static void Execute(SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    private static void FlushToDisk(string path)
    {
        using var file = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite);
        file.Flush(flushToDisk: true);
    }

    private static void DeleteIfPresent(string path)
    {
        if (File.Exists(path))
        {
            File.Delete(path);
        }
    }
}
