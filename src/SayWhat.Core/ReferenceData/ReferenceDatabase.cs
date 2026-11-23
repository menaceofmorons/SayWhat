using Microsoft.Data.Sqlite;

namespace SayWhat.Core.ReferenceData;

public sealed record ShipNameEntry(int TypeId, string Name);

public sealed class ReferenceDatabase
{
    public const string DefaultShipLanguage = "en";

    public ReferenceDatabase(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        Path = path;
    }

    public string Path { get; }

    public bool Exists => File.Exists(Path);

    public string? SdeVersion => Exists ? First("SELECT sde_version FROM sde_build LIMIT 1", _ => { }, reader => reader.GetString(0)) : null;

    public IReadOnlyList<string> ClientLanguages() =>
        Query("SELECT DISTINCT language FROM ship_name ORDER BY language", _ => { }, reader => reader.GetString(0));

    public IReadOnlyList<ShipNameEntry> ShipNames(string language) =>
        Query(
            "SELECT type_id, name FROM ship_name WHERE language = $language ORDER BY length(name) DESC, name, type_id",
            command => command.Parameters.AddWithValue("$language", language),
            reader => new ShipNameEntry(reader.GetInt32(0), reader.GetString(1)));

    public int? FindShipType(string language, string name) =>
        First(
            "SELECT type_id FROM ship_name WHERE language = $language AND normalised_name = $name ORDER BY type_id LIMIT 1",
            command =>
            {
                command.Parameters.AddWithValue("$language", language);
                command.Parameters.AddWithValue("$name", NameNormaliser.Normalise(name));
            },
            reader => (int?)reader.GetInt32(0));

    public string? ShipName(int typeId, string language) =>
        First(
            "SELECT name FROM ship_name WHERE type_id = $type AND language = $language",
            command =>
            {
                command.Parameters.AddWithValue("$type", typeId);
                command.Parameters.AddWithValue("$language", language);
            },
            reader => reader.GetString(0));

    public bool IsClientLanguage(string language) => ClientLanguages().Contains(language, StringComparer.Ordinal);

    public string? TranslateShipName(string sourceLanguage, string name, string targetLanguage, string shipNameLanguage = DefaultShipLanguage)
    {
        var typeId = FindShipType(sourceLanguage, name);
        if (typeId is null)
        {
            return null;
        }

        var language = IsClientLanguage(targetLanguage) ? targetLanguage : shipNameLanguage;
        return ShipName(typeId.Value, language);
    }

    public IReadOnlyList<string> SystemNames(string language) =>
        Query(
            "SELECT name FROM system_name WHERE language = $language ORDER BY length(name) DESC, name",
            command => command.Parameters.AddWithValue("$language", language),
            reader => reader.GetString(0));

    public int? FindSystem(string language, string name) =>
        First(
            "SELECT system_id FROM system_name WHERE language = $language AND name = $name COLLATE NOCASE ORDER BY system_id LIMIT 1",
            command =>
            {
                command.Parameters.AddWithValue("$language", language);
                command.Parameters.AddWithValue("$name", name.Trim());
            },
            reader => (int?)reader.GetInt32(0));

    public string? SystemName(int systemId, string language) =>
        First(
            "SELECT name FROM system_name WHERE system_id = $system AND language = $language",
            command =>
            {
                command.Parameters.AddWithValue("$system", systemId);
                command.Parameters.AddWithValue("$language", language);
            },
            reader => reader.GetString(0));

    private T? First<T>(string sql, Action<SqliteCommand> bind, Func<SqliteDataReader, T> read)
    {
        var rows = Query(sql, bind, read);
        return rows.Count == 0 ? default : rows[0];
    }

    private List<T> Query<T>(string sql, Action<SqliteCommand> bind, Func<SqliteDataReader, T> read)
    {
        using var connection = ReferenceSchema.Open(Path, SqliteOpenMode.ReadOnly);
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        bind(command);
        using var reader = command.ExecuteReader();
        var rows = new List<T>();
        while (reader.Read())
        {
            rows.Add(read(reader));
        }

        return rows;
    }
}
