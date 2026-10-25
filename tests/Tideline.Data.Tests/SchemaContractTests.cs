using System.Text.RegularExpressions;
using FluentAssertions;
using Microsoft.Data.Sqlite;

namespace Tideline.Data.Tests;

/// <summary>The migrations must produce exactly the structure in docs/db/schema.sqlite.sql.</summary>
public class SchemaContractTests
{
    private static List<(string Type, string Name, string Sql)> Objects(SqliteConnection conn)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT type, name, sql FROM sqlite_master
            WHERE sql IS NOT NULL AND name NOT LIKE 'sqlite_%' AND name NOT LIKE '%VersionInfo%' AND name <> 'UC_Version'
            ORDER BY type, name
            """;
        var rows = new List<(string, string, string)>();
        using var r = cmd.ExecuteReader();
        while (r.Read()) rows.Add((r.GetString(0), r.GetString(1), Normalize(r.GetString(2))));
        return rows;
    }

    private static string Normalize(string sql) => Regex.Replace(sql, @"\s+", " ").Trim();

    private static List<string> Columns(SqliteConnection conn, string table)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = $"SELECT name, type, \"notnull\", dflt_value, pk FROM pragma_table_info('{table}') ORDER BY cid";
        var cols = new List<string>();
        using var r = cmd.ExecuteReader();
        while (r.Read())
            cols.Add($"{table}.{r.GetString(0)} {r.GetString(1)} notnull={r.GetInt32(2)} default={(r.IsDBNull(3) ? "-" : r.GetString(3))} pk={r.GetInt32(4)}");
        return cols;
    }

    [Fact]
    public void Migrated_schema_has_the_same_tables_indexes_and_triggers_as_the_contract()
    {
        using var migrated = SqliteTestDb.Migrated();
        using var contract = SqliteTestDb.FromContract();
        using var a = migrated.Open();
        using var b = contract.Open();

        var fromMigrations = Objects(a);
        var fromContract = Objects(b);

        fromContract.Should().NotBeEmpty();
        fromMigrations.Select(o => (o.Type, o.Name)).Should().BeEquivalentTo(fromContract.Select(o => (o.Type, o.Name)));
        foreach (var (type, name, sql) in fromContract)
            fromMigrations.Single(o => o.Type == type && o.Name == name).Sql.Should().Be(sql, $"{type} {name} must match the contract");
    }

    [Fact]
    public void Every_column_matches_in_type_nullability_default_and_key()
    {
        using var migrated = SqliteTestDb.Migrated();
        using var contract = SqliteTestDb.FromContract();
        using var a = migrated.Open();
        using var b = contract.Open();

        foreach (var table in Objects(b).Where(o => o.Type == "table").Select(o => o.Name))
            Columns(a, table).Should().Equal(Columns(b, table), $"columns of {table}");
    }

    [Fact]
    public void Migrating_twice_is_a_no_op()
    {
        using var db = SqliteTestDb.Migrated();
        FluentActions.Invoking(() => Migrator.MigrateUp(db.ConnectionString)).Should().NotThrow();
    }
}

/// <summary>The guard triggers are non-negotiable: bank rows are never edited or deleted, the audit log is append-only.</summary>
public class GuardTriggerTests : IDisposable
{
    private readonly SqliteTestDb _db = SqliteTestDb.Migrated();
    private readonly SqliteConnection _conn;

    public GuardTriggerTests()
    {
        _conn = _db.Open();
        SqliteTestDb.Exec(_conn, "INSERT INTO accounts (id, name, kind, created_at) VALUES (1, 'Checking', 'checking', '2026-01-01T00:00:00Z')");
        SqliteTestDb.Exec(_conn, "INSERT INTO categories (id, name, default_type) VALUES (1, 'Uncategorized', 'extra')");
        Insert(10, "truist:aaa", "csv");
        Insert(11, "manual:bbb", "manual");
    }

    private void Insert(int id, string externalId, string source) =>
        SqliteTestDb.Exec(_conn, """
            INSERT INTO transactions (id, account_id, posted_on, amount_cents, original_description, external_id, source,
                                      category_id, type, created_at, updated_at)
            VALUES (@id, 1, '2026-01-02', -1234, 'COFFEE SHOP', @ext, @src, 1, 'extra', '2026-01-02T00:00:00Z', '2026-01-02T00:00:00Z')
            """, ("@id", id), ("@ext", externalId), ("@src", source));

    [Theory]
    [InlineData("posted_on = '2026-02-02'")]
    [InlineData("amount_cents = -1")]
    [InlineData("original_description = 'OTHER'")]
    [InlineData("bank_balance_cents = 5")]
    [InlineData("check_number = '12'")]
    [InlineData("external_id = 'truist:zzz'")]
    [InlineData("source = 'manual'")]
    [InlineData("account_id = 1, amount_cents = -1234, posted_on = '2026-02-02'")]
    public void Bank_fields_cannot_be_updated(string set) =>
        FluentActions.Invoking(() => SqliteTestDb.Exec(_conn, $"UPDATE transactions SET {set} WHERE id = 10"))
            .Should().Throw<SqliteException>().WithMessage("*bank fields are read-only*");

    [Fact]
    public void Our_fields_can_be_updated() =>
        FluentActions.Invoking(() => SqliteTestDb.Exec(_conn, "UPDATE transactions SET payee = 'Joe', notes = 'x', type = 'required', is_manual_override = 1 WHERE id = 10"))
            .Should().NotThrow();

    [Fact]
    public void Imported_transactions_cannot_be_deleted() =>
        FluentActions.Invoking(() => SqliteTestDb.Exec(_conn, "DELETE FROM transactions WHERE id = 10"))
            .Should().Throw<SqliteException>().WithMessage("*imported transactions cannot be deleted*");

    [Fact]
    public void Manual_transactions_can_be_deleted() =>
        FluentActions.Invoking(() => SqliteTestDb.Exec(_conn, "DELETE FROM transactions WHERE id = 11"))
            .Should().NotThrow();

    [Fact]
    public void Audit_log_is_append_only()
    {
        SqliteTestDb.Exec(_conn, "INSERT INTO audit_log (id, at, action, entity) VALUES (1, '2026-01-01T00:00:00Z', 'x.y', 'x')");
        FluentActions.Invoking(() => SqliteTestDb.Exec(_conn, "UPDATE audit_log SET action = 'z' WHERE id = 1"))
            .Should().Throw<SqliteException>().WithMessage("*append-only*");
        FluentActions.Invoking(() => SqliteTestDb.Exec(_conn, "DELETE FROM audit_log WHERE id = 1"))
            .Should().Throw<SqliteException>().WithMessage("*append-only*");
    }

    [Fact]
    public void A_line_is_either_a_move_or_has_a_type() =>
        FluentActions.Invoking(() => SqliteTestDb.Exec(_conn, """
            INSERT INTO transactions (account_id, posted_on, amount_cents, original_description, external_id, source, created_at, updated_at)
            VALUES (1, '2026-01-02', -1, 'NO TYPE', 'truist:nt', 'csv', '2026-01-02T00:00:00Z', '2026-01-02T00:00:00Z')
            """)).Should().Throw<SqliteException>();

    public void Dispose()
    {
        _conn.Dispose();
        _db.Dispose();
    }
}
