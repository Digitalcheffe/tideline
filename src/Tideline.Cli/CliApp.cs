using Dapper;
using Tideline.Core;
using Tideline.Data;
using Tideline.Data.Repositories;
using Tideline.Data.Security;
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
          backup [--out dir] [--keys dir]
                                          copy the database (and the keys folder) into dated files, keep 30 days, then check the copy
          backup --verify <file> [--year 2026]
                                          open a copy, run the integrity check and compare its spread with the live database
          restore <file> [--keys-tar file --keys-dir dir] [--force] [--year 2026]
                                          put a copy back as the database named by --db, then check it
          user <add|list|link|reset-mfa|role|deactivate>
                                          people who can sign in; `tideline user` shows the details
          demo <folder> [--lines 5000] [--seed 42] [--from 2025-01-01] [--to 2026-09-30] [--load]
                                          write made-up exports for six accounts (and a household file) to try the app;
                                          with --load, also set up and import them into the database

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

        // account numbers are kept as keyed hashes; the key file sits with the other keys, beside the database unless told otherwise
        AccountNumbers.ConfigureOnce(() => AccountNumberKeys.LoadOrCreate(Path.Combine(KeysFolder(options, connectionString), "account-numbers.key")));

        try
        {
            if (command is "setup" or "import" or "status" or "lines" or "backup" && File.Exists(new Microsoft.Data.Sqlite.SqliteConnectionStringBuilder(connectionString).DataSource))
            {
                using var check = new SqliteConnectionFactory(connectionString).Open();
                if (command != "backup") AccountNumbers.Verify(check, clock);
            }

            switch (command)
            {
                case "migrate":
                    EnsureDirectory(connectionString);
                    Migrator.MigrateUp(connectionString);
                    using (var db = new SqliteConnectionFactory(connectionString).Open())
                    {
                        AccountNumbers.Verify(db, clock);
                        var secured = AccountNumbers.SecureExisting(db, clock);
                        if (secured > 0) output.WriteLine($"Account numbers: {secured} kept only as a hash and a sealed last 4 digits.");
                    }
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
                case "user":
                    return UserCommands.Run(rest, options, connectionString, clock, output);
                case "backup":
                    return Backup(connectionString, options);
                case "restore":
                    if (rest.Count != 1) return Bad("restore needs one backup file: tideline restore backups/tideline-20261025.db --db \"Data Source=new.db\"");
                    return Restore(rest[0], connectionString, options);
                case "demo":
                    if (rest.Count != 1) return Bad("demo needs one folder: tideline demo ./demo-data");
                    return await DemoAsync(rest[0], options, connectionString);
                default:
                    return Bad($"unknown command '{command}'.");
            }
        }
        catch (Exception e) when (e is InvalidDataException or Microsoft.Data.Sqlite.SqliteException { SqliteErrorCode: 19 } or FileNotFoundException or DirectoryNotFoundException or System.Text.Json.JsonException or FormatException)
        {
            output.WriteLine($"Error: {e.Message}");
            return 1;
        }
    }

    /// <summary>
    /// Where the keys are: --keys, else the same setting the app reads (Tideline__DataProtectionKeys, which the container sets), else beside the
    /// database. The command line and the app have to agree, or the tool would make a second set of keys the app never uses.
    /// </summary>
    private static string KeysFolder(IReadOnlyDictionary<string, string?> options, string connectionString) =>
        options.GetValueOrDefault("keys") ?? Environment.GetEnvironmentVariable("Tideline__DataProtectionKeys") ?? BesideDatabase(connectionString, "keys");

    private static string BesideDatabase(string connectionString, string folder) =>
        Path.Combine(Path.GetDirectoryName(Path.GetFullPath(new Microsoft.Data.Sqlite.SqliteConnectionStringBuilder(connectionString).DataSource))!, folder);

    private int Backup(string connectionString, Dictionary<string, string?> options)
    {
        var year = options.TryGetValue("year", out var y) && int.TryParse(y, out var n) ? n : clock.GetUtcNow().Year;
        if (options.TryGetValue("verify", out var file))
        {
            if (string.IsNullOrWhiteSpace(file)) return Bad("--verify needs a backup file.");
            var check = Data.Services.BackupService.Verify(file, connectionString, year);
            output.WriteLine(check.Message);
            output.WriteLine($"{year} spread in the copy: in {Dollars(check.Backup.InCents)}, out {Dollars(check.Backup.OutCents)}, left over {Dollars(check.Backup.LeftOverCents)}");
            return check.Ok ? 0 : 1;
        }

        var outDir = options.GetValueOrDefault("out") ?? Environment.GetEnvironmentVariable("Tideline__BackupPath") ?? BesideDatabase(connectionString, "backups");
        var keys = KeysFolder(options, connectionString);
        var made = Data.Services.BackupService.Create(connectionString, outDir, keys, clock.GetUtcNow());
        output.WriteLine($"Database copy: {made.DatabaseFile} ({made.Bytes / 1024} KB)");
        output.WriteLine(made.KeysFile is null ? $"Keys: none found in {keys}, so nothing was copied." : $"Keys: {made.KeysFile}");
        foreach (var gone in made.Removed) output.WriteLine($"Removed old copy {gone}");

        var verified = Data.Services.BackupService.Verify(made.DatabaseFile, connectionString, year);
        output.WriteLine(verified.Message);
        return verified.Ok ? 0 : 1;
    }

    private int Restore(string file, string connectionString, Dictionary<string, string?> options)
    {
        var year = options.TryGetValue("year", out var y) && int.TryParse(y, out var n) ? n : clock.GetUtcNow().Year;
        var result = Data.Services.BackupService.Restore(file, connectionString, options.GetValueOrDefault("keys-tar"), options.GetValueOrDefault("keys-dir"), options.ContainsKey("force"), year);
        output.WriteLine($"Restored to {result.DatabaseFile}");
        if (result.KeysDirectory is not null) output.WriteLine($"Keys restored to {result.KeysDirectory}");
        output.WriteLine($"{year} spread after the restore: in {Dollars(result.Check.Backup.InCents)}, out {Dollars(result.Check.Backup.OutCents)}, left over {Dollars(result.Check.Backup.LeftOverCents)}");
        output.WriteLine(result.Check.Ok ? "The restored database passes the integrity check." : result.Check.Message);
        return result.Check.Ok ? 0 : 1;
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
        files = ImportOrder.OldestFirst(files).ToList();

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

    private async Task<int> DemoAsync(string folder, Dictionary<string, string?> options, string connectionString)
    {
        var d = DemoOptions.Default;
        var o = d with
        {
            Lines = options.GetValueOrDefault("lines") is { } l ? int.Parse(l) : d.Lines,
            Seed = options.GetValueOrDefault("seed") is { } sd ? int.Parse(sd) : d.Seed,
            From = options.GetValueOrDefault("from") is { } f ? DateOnly.Parse(f) : d.From,
            To = options.GetValueOrDefault("to") is { } t ? DateOnly.Parse(t) : d.To,
        };
        var result = DemoData.Generate(folder, o);

        output.WriteLine($"Wrote {result.Lines:N0} made-up lines in {result.Files} files to {Path.GetFullPath(folder)}");
        foreach (var (account, count) in result.LinesPerAccount) output.WriteLine($"  {account,-20} {count,5} lines");
        output.WriteLine($"Household file: {result.HouseholdFile}");

        if (!options.ContainsKey("load"))
        {
            output.WriteLine();
            output.WriteLine("To load it: tideline migrate, then tideline setup <household file>, then tideline import <folder> (or run demo again with --load).");
            return 0;
        }

        output.WriteLine();
        EnsureDirectory(connectionString);
        Migrator.MigrateUp(connectionString);
        var clock2 = clock;
        foreach (var line in HouseholdSetup.Apply(HouseholdSetup.Parse(File.ReadAllText(result.HouseholdFile)), new SqliteConnectionFactory(connectionString), clock2))
            if (!line.EndsWith("already there", StringComparison.Ordinal)) { /* quiet: the household is long */ }
        output.WriteLine("Household set up.");
        return await ImportAsync([folder], connectionString);
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
        var bank = AccountNumbers.HashOf(options.GetValueOrDefault("account")) ?? options.GetValueOrDefault("account")?.Length switch { null => null, _ => "no such account" };
        var rows = conn.Query<(string Posted, long Cents, string Desc, string? Payee, string? Category, string? Type, long? MoveId, string Account)>("""
            SELECT t.posted_on, t.amount_cents, t.original_description, t.payee, c.name, t.type, t.move_id, a.name
            FROM transactions t JOIN accounts a ON a.id = t.account_id LEFT JOIN categories c ON c.id = t.category_id
            WHERE (@bank IS NULL OR a.bank_number_hash = @bank)
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
