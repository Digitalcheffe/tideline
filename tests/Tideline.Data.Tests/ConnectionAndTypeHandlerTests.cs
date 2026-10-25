using Dapper;
using FluentAssertions;
using Tideline.Core;

namespace Tideline.Data.Tests;

public class ConnectionAndTypeHandlerTests : IDisposable
{
    private readonly SqliteTestDb _db = SqliteTestDb.Migrated();
    private SqliteConnectionFactory Factory => new(_db.ConnectionString);

    [Fact]
    public void Every_connection_has_the_required_pragmas()
    {
        using var conn = Factory.Open();
        conn.ExecuteScalar<long>("PRAGMA foreign_keys").Should().Be(1);
        conn.ExecuteScalar<long>("PRAGMA busy_timeout").Should().Be(5000);
        conn.ExecuteScalar<string>("PRAGMA journal_mode").Should().Be("wal");
    }

    [Fact]
    public void Foreign_keys_are_enforced()
    {
        using var conn = Factory.Open();
        FluentActions.Invoking(() => conn.Execute(
                "INSERT INTO card_terms (account_id) VALUES (999)"))
            .Should().Throw<Microsoft.Data.Sqlite.SqliteException>();
    }

    [Fact]
    public void Dates_are_stored_as_plain_text_and_round_trip()
    {
        using var conn = Factory.Open();
        conn.Execute("CREATE TEMP TABLE t (d TEXT, dn TEXT)");
        conn.Execute("INSERT INTO t VALUES (@d, @dn)", new { d = new DateOnly(2026, 3, 9), dn = (DateOnly?)null });

        conn.ExecuteScalar<string>("SELECT d FROM t").Should().Be("2026-03-09");
        var row = conn.QuerySingle<(DateOnly D, DateOnly? Dn)>("SELECT d AS D, dn AS Dn FROM t");
        row.D.Should().Be(new DateOnly(2026, 3, 9));
        row.Dn.Should().BeNull();
    }

    [Fact]
    public void Instants_are_stored_as_utc_iso_text_and_round_trip()
    {
        using var conn = Factory.Open();
        conn.Execute("CREATE TEMP TABLE t (at TEXT)");
        var local = new DateTimeOffset(2026, 3, 9, 8, 30, 5, TimeSpan.FromHours(-5));
        conn.Execute("INSERT INTO t VALUES (@at)", new { at = local });

        conn.ExecuteScalar<string>("SELECT at FROM t").Should().Be("2026-03-09T13:30:05Z");
        conn.ExecuteScalar<DateTimeOffset>("SELECT at FROM t").Should().Be(local);
    }

    [Theory]
    [InlineData(LineType.OneTime, "one_time")]
    [InlineData(LineType.Income, "income")]
    public void Enums_are_stored_as_snake_case_and_round_trip(LineType type, string text)
    {
        using var conn = Factory.Open();
        conn.Execute("CREATE TEMP TABLE t (type TEXT)");
        conn.Execute("INSERT INTO t VALUES (@type)", new { type = type.ToDb() });

        conn.ExecuteScalar<string>("SELECT type FROM t").Should().Be(text);
        TypeHandlers.FromDb<LineType>(conn.ExecuteScalar<string>("SELECT type FROM t")!).Should().Be(type);
    }

    [Fact]
    public void Unknown_enum_text_is_rejected() =>
        FluentActions.Invoking(() => TypeHandlers.FromDb<LineType>("oneTime")).Should().Throw<InvalidDataException>();

    [Fact]
    public async Task Write_gate_lets_one_writer_in_at_a_time()
    {
        var gate = new WriteGate();
        var first = await gate.EnterAsync();
        var second = gate.EnterAsync();
        second.IsCompleted.Should().BeFalse();
        first.Dispose();
        first.Dispose(); // double dispose must not release twice
        (await second).Dispose();
        var third = await gate.EnterAsync();
        third.Dispose();
    }

    public void Dispose() => _db.Dispose();
}
