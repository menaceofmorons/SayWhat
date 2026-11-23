using Microsoft.Data.Sqlite;

namespace SayWhat.Core.ReferenceData;

internal static class ReferenceSchema
{
    private const string Statements = """
        CREATE TABLE sde_build (
            sde_version TEXT NOT NULL,
            imported_at TEXT NOT NULL
        );
        CREATE TABLE ship_name (
            type_id INTEGER NOT NULL,
            language TEXT NOT NULL,
            name TEXT NOT NULL,
            normalised_name TEXT NOT NULL,
            PRIMARY KEY (type_id, language)
        );
        CREATE INDEX ix_ship_name_lookup ON ship_name (language, normalised_name);
        CREATE TABLE system_name (
            system_id INTEGER NOT NULL,
            language TEXT NOT NULL,
            name TEXT NOT NULL,
            PRIMARY KEY (system_id, language)
        );
        CREATE INDEX ix_system_name_lookup ON system_name (language, name COLLATE NOCASE);
        """;

    public static void Create(SqliteConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText = Statements;
        command.ExecuteNonQuery();
    }

    public static SqliteConnection Open(string path, SqliteOpenMode mode)
    {
        var builder = new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Mode = mode,
            Pooling = false,
        };
        var connection = new SqliteConnection(builder.ConnectionString);
        connection.Open();
        return connection;
    }
}
