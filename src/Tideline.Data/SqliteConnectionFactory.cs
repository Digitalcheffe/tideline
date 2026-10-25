using Microsoft.Data.Sqlite;

namespace Tideline.Data;

/// <summary>Opens SQLite connections with the pragmas from docs/03-database.md on every one.</summary>
public sealed class SqliteConnectionFactory(string connectionString)
{
    public string ConnectionString { get; } = connectionString;

    public SqliteConnection Open()
    {
        TypeHandlers.EnsureRegistered();
        var conn = new SqliteConnection(ConnectionString);
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "PRAGMA foreign_keys = ON; PRAGMA journal_mode = WAL; PRAGMA busy_timeout = 5000;";
        cmd.ExecuteNonQuery();
        return conn;
    }
}

/// <summary>
/// SQLite allows one writer at a time. Background jobs and API writes share this gate around their
/// write transactions so they queue in-process instead of hitting "database is locked".
/// </summary>
public sealed class WriteGate
{
    private readonly SemaphoreSlim _gate = new(1, 1);

    public async Task<IDisposable> EnterAsync(CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct);
        return new Releaser(_gate);
    }

    private sealed class Releaser(SemaphoreSlim gate) : IDisposable
    {
        private int _released;
        public void Dispose()
        {
            if (Interlocked.Exchange(ref _released, 1) == 0) gate.Release();
        }
    }
}
