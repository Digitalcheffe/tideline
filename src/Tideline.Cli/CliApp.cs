using Dapper;
using Tideline.Core;
using Tideline.Data;
using Tideline.Data.Repositories;
using Tideline.Import;

namespace Tideline.Cli;

/// <summary>The <c>tideline</c> command. Phase 1 commands: migrate, setup, import, status, lines.</summary>
public sealed class CliApp(TextWriter output, TimeProvider clock)
{
    private const string DefaultConnection = "Data Source=tideline.db";

    public const string Usage = """
        tideline <command> [options]

          migrate                         create or update the database
          setup <household.json>          add accounts, categories, rules and move patterns from a local file
          import <file-or-folder>...      import Truist CSV exports (folders: every acct_*.csv inside)
          status                          every account: ledger balance against the bank's last balance
          lines [--account 1234] [--random N | --take N]
                                          show ledger lines, to check a few against the bank

        The database is Database__ConnectionString (default: Data Source=tideline.db), or --db "<connection string>".
        """;

    public async Task<int> RunAsync(string[] args)
    {
        var (positional, options) = ParseArgs(args);
        if (positional.Count == 0 || options.ContainsKey("help"))
        {
            output.WriteLine(Usage);
            return positional.Count == 0 && !options.ContainsKey("help") ? 1 : 0;
        }

        var connectionString = options.GetValueOrDefault("db")
            ?? Environment.GetEnvironmentVariable("Database__ConnectionString")
            ?? DefaultConnection;
        var command = positional[0];
        var rest = positional.Skip(1).ToList();

        try
        {
            switch (command)
            {
                case "migrate":
                    EnsureDirectory(connectionString);
                    Migrator.MigrateUp(connectionString);
                    output.WriteLine("Database is up to date.");
                    return 0;
                case "setup":
                    if (rest.Count != 1) return Bad("setup needs one file: tideline setup household.json");
                    return Setup(rest[0], connectionString);
                case "import":
                    if (rest.Count == 0) return Bad("import needs at least one file or folder.");
                    return await ImportAsync(rest, connectionString);
                case "status":
                    return Status(connectionString);
                case "lines":
                    return Lines(connectionString, options);
                default:
                    return Bad($"unknown command '{command}'.");
            }
        }
        catch (Exception e) when (e is InvalidDataException or FileNotFoundException or DirectoryNotFoundException or System.Text.Json.JsonException or FormatException)
        {
            output.WriteLine($"Error: {e.Message}");
            return 1;
        }
    }

    private int Bad(string message)
    {
        output.WriteLine($"Error: {message}");
        output.WriteLine();
        output.WriteLine(Usage);
        return 1;
    }

    private static void EnsureDirectory(string connectionString)
    {
        var builder = new Microsoft.Data.Sqlite.SqliteConnectionStringBuilder(connectionString);
        var dir = Path.GetDirectoryName(Path.GetFullPath(builder.DataSource));
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
    }

    private int Setup(string file, string connectionString)
    {
        var household = HouseholdSetup.Parse(File.ReadAllText(file));
        foreach (var line in HouseholdSetup.Apply(household, new SqliteConnectionFactory(connectionString), clock))
            output.WriteLine(line);
        return 0;
    }

    private async Task<int> ImportAsync(List<string> paths, string connectionString)
    {
        var files = new List<string>();
        foreach (var p in paths)
        {
            if (Directory.Exists(p))
                files.AddRange(Directory.EnumerateFiles(p, "acct_*.csv").OrderBy(f => f, StringComparer.OrdinalIgnoreCase));
            else if (File.Exists(p)) files.Add(p);
            else throw new FileNotFoundException($"'{p}' is not a file or folder.");
        }
        if (files.Count == 0) throw new FileNotFoundException("No acct_*.csv files found.");

        var pipeline = new ImportPipeline(new SqliteConnectionFactory(connectionString), new WriteGate(), clock);
        int failed = 0, inserted = 0, review = 0;
        foreach (var f in files)
        {
            var r = await pipeline.ImportTruistAsync(f, await File.ReadAllTextAsync(f));
            if (r.Succeeded)
            {
                inserted += r.RowsInserted;
                review += r.RowsReview;
                var rec = r.Reconciliation is { } x ? (x.Matches ? $", balance matches the bank ({Dollars(x.BankCents)})" : $", BALANCE DIFFERS: bank {Dollars(x.BankCents)}, ledger {Dollars(x.LedgerCents)}") : "";
                output.WriteLine($"{r.FileName}: {r.RowsInserted} new, {r.RowsExisting} already there, {r.RowsReview} to review, {r.MovesPaired} moves paired{rec}");
            }
            else
            {
                failed++;
                output.WriteLine($"{r.FileName}: FAILED. {r.Error}");
            }
        }
        output.WriteLine();
        output.WriteLine($"{files.Count - failed} of {files.Count} files imported, {inserted} new lines, {review} to review.");
        return failed == 0 ? 0 : 2;
    }

    private int Status(string connectionString)
    {
        using var conn = new SqliteConnectionFactory(connectionString).Open();
        var ledger = new LedgerRepository(conn, clock);
        var snaps = new BalanceSnapshotRepository(conn, clock);

        output.WriteLine($"{"Account",-24} {"Kind",-12} {"Lines",6} {"Ledger",14} {"Bank",14}  Match");
        foreach (var a in new AccountRepository(conn, clock).All())
        {
            var count = conn.ExecuteScalar<long>("SELECT COUNT(*) FROM transactions WHERE account_id = @id", new { id = a.Id });
            var last = conn.ExecuteScalar<string?>("SELECT MAX(posted_on) FROM transactions WHERE account_id = @id", new { id = a.Id });
            var balance = last is null ? ledger.BalanceAsOf(a.Id, DateOnly.MinValue) : ledger.BalanceAsOf(a.Id, DateOnly.ParseExact(last, "yyyy-MM-dd"));
            var bank = snaps.LatestBank(a.Id);
            var match = bank is null ? "-" : bank == balance ? "yes" : "NO";
            output.WriteLine($"{a.Name,-24} {a.Kind.ToDb(),-12} {count,6} {Dollars(balance),14} {(bank is null ? "-" : Dollars(bank.Value)),14}  {match}");
        }

        var review = conn.ExecuteScalar<long>("""
            SELECT COUNT(DISTINCT t.id) FROM transactions t
            JOIN transaction_tags tt ON tt.transaction_id = t.id JOIN tags g ON g.id = tt.tag_id WHERE g.name = 'review'
            """);
        var moves = conn.ExecuteScalar<long>("SELECT COUNT(*) FROM (SELECT move_id FROM transactions WHERE move_id IS NOT NULL GROUP BY move_id HAVING COUNT(*) = 2)");
        var pending = ledger.PendingMoveLegs().Count;
        output.WriteLine();
        output.WriteLine($"Moves paired: {moves}. Waiting for a partner: {pending}. Lines tagged review: {review}.");
        return 0;
    }

    private int Lines(string connectionString, Dictionary<string, string?> options)
    {
        using var conn = new SqliteConnectionFactory(connectionString).Open();
        var bank = options.GetValueOrDefault("account");
        var rows = conn.Query<(string Posted, long Cents, string Desc, string? Payee, string? Category, string? Type, long? MoveId, string Account)>("""
            SELECT t.posted_on, t.amount_cents, t.original_description, t.payee, c.name, t.type, t.move_id, a.name
            FROM transactions t JOIN accounts a ON a.id = t.account_id LEFT JOIN categories c ON c.id = t.category_id
            WHERE (@bank IS NULL OR a.bank_number = @bank)
            ORDER BY t.posted_on, t.id
            """, new { bank }).ToList();

        IEnumerable<(string Posted, long Cents, string Desc, string? Payee, string? Category, string? Type, long? MoveId, string Account)> shown = rows;
        if (options.GetValueOrDefault("random") is { } r) shown = rows.OrderBy(_ => Random.Shared.Next()).Take(int.Parse(r)).OrderBy(x => x.Posted);
        else if (options.GetValueOrDefault("take") is { } t) shown = rows.Take(int.Parse(t));

        foreach (var l in shown)
        {
            var meaning = l.MoveId is not null ? $"move #{l.MoveId}" : $"{l.Type} / {l.Category} / {l.Payee}";
            output.WriteLine($"{l.Posted}  {l.Account,-14} {Dollars(l.Cents),12}  {l.Desc,-34} {meaning}");
        }
        return 0;
    }

    public static string Dollars(long cents) => new Money(cents).ToString();

    private static (List<string> Positional, Dictionary<string, string?> Options) ParseArgs(string[] args)
    {
        var positional = new List<string>();
        var options = new Dictionary<string, string?>();
        for (var i = 0; i < args.Length; i++)
        {
            if (args[i].StartsWith("--"))
            {
                var key = args[i][2..];
                var hasValue = i + 1 < args.Length && !args[i + 1].StartsWith("--") && key != "help";
                options[key] = hasValue ? args[++i] : null;
            }
            else positional.Add(args[i]);
        }
        return (positional, options);
    }
}
