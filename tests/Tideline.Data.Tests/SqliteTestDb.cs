using Microsoft.Data.Sqlite;

namespace Tideline.Data.Tests;

/// <summary>A throwaway SQLite file, deleted on dispose.</summary>
public sealed class SqliteTestDb : IDisposable
{
    private readonly string _path = Path.Combine(Path.GetTempPath(), $"tideline-test-{Guid.NewGuid():N}.db");

    public string ConnectionString => $"Data Source={_path};Pooling=False";

    public static SqliteTestDb Migrated()
    {
        var db = new SqliteTestDb();
        Migrator.MigrateUp(db.ConnectionString);
        return db;
    }

    public static SqliteTestDb FromContract()
    {
        var db = new SqliteTestDb();
        using var conn = db.Open();
        Exec(conn, File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "schema.sqlite.sql")));
        return db;
    }

    public SqliteConnection Open()
    {
        var conn = new SqliteConnection(ConnectionString);
        conn.Open();
        Exec(conn, "PRAGMA foreign_keys = ON;");
        return conn;
    }

    public static void Exec(SqliteConnection conn, string sql, params (string Name, object? Value)[] args)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        foreach (var (name, value) in args) cmd.Parameters.AddWithValue(name, value ?? DBNull.Value);
        cmd.ExecuteNonQuery();
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        foreach (var f in new[] { _path, _path + "-wal", _path + "-shm" })
            if (File.Exists(f)) File.Delete(f);
    }
}
