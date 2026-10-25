using FluentAssertions;
using Microsoft.Data.Sqlite;
using Tideline.Data.Services;

namespace Tideline.Cli.Tests;

public sealed class BackupTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"tideline-backup-{Guid.NewGuid():N}");
    private readonly DateTimeOffset _now = new(2026, 4, 1, 9, 0, 0, TimeSpan.Zero);

    public BackupTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
    }

    private static string Fixtures => Path.Combine(AppContext.BaseDirectory, "fixtures");
    private string Live => $"Data Source={Path.Combine(_dir, "live", "t.db")};Pooling=False";

    private async Task<(int Code, string Text)> Run(string connectionString, params string[] args)
    {
        var sw = new StringWriter();
        var code = await new CliApp(sw, new FixedClock(_now)).RunAsync([.. args, "--db", connectionString]);
        return (code, sw.ToString());
    }

    private async Task LoadHousehold()
    {
        (await Run(Live, "migrate")).Code.Should().Be(0);
        (await Run(Live, "setup", Path.Combine(Fixtures, "household.json"))).Code.Should().Be(0);
        foreach (var f in new[] { "acct_1111_03_01_2026_to_03_31_2026.csv", "acct_3333_03_01_2026_to_03_31_2026.csv", "acct_2222_03_01_2026_to_03_31_2026.csv" })
            (await Run(Live, "import", Path.Combine(Fixtures, "truist", f))).Code.Should().Be(0);
    }

    [Fact]
    public async Task A_backup_is_a_dated_copy_with_the_keys_beside_it_and_it_checks_out()
    {
        await LoadHousehold();
        var keys = Path.Combine(_dir, "keys");
        Directory.CreateDirectory(keys);
        await File.WriteAllTextAsync(Path.Combine(keys, "key-1.xml"), "<key/>");
        var outDir = Path.Combine(_dir, "backups");

        var made = await Run(Live, "backup", "--out", outDir, "--keys", keys, "--year", "2026");

        made.Code.Should().Be(0, made.Text);
        File.Exists(Path.Combine(outDir, "tideline-20260401.db")).Should().BeTrue();
        File.Exists(Path.Combine(outDir, "keys-20260401.tar")).Should().BeTrue();
        made.Text.Should().Contain("same 2026 spread as the live database");
    }

    [Fact]
    public async Task Copies_older_than_thirty_days_are_removed_and_newer_ones_kept()
    {
        await LoadHousehold();
        var outDir = Path.Combine(_dir, "backups");
        Directory.CreateDirectory(outDir);
        foreach (var name in new[] { "tideline-20260101.db", "keys-20260101.tar", "tideline-20260305.db", "tideline-20260302.db", "notes.txt" })
            await File.WriteAllTextAsync(Path.Combine(outDir, name), "x");

        var made = await Run(Live, "backup", "--out", outDir, "--keys", Path.Combine(_dir, "no-keys"));

        made.Code.Should().Be(0, made.Text);
        Directory.GetFiles(outDir).Select(Path.GetFileName).Should().BeEquivalentTo("tideline-20260401.db", "tideline-20260305.db", "tideline-20260302.db", "notes.txt");
        made.Text.Should().Contain("Removed old copy tideline-20260101.db").And.Contain("none found");
    }

    [Fact]
    public async Task A_restore_puts_back_a_database_with_the_same_spread_and_will_not_overwrite_by_accident()
    {
        await LoadHousehold();
        var keys = Path.Combine(_dir, "keys");
        Directory.CreateDirectory(keys);
        await File.WriteAllTextAsync(Path.Combine(keys, "key-1.xml"), "<key>secret</key>");
        var outDir = Path.Combine(_dir, "backups");
        (await Run(Live, "backup", "--out", outDir, "--keys", keys)).Code.Should().Be(0);

        var elsewhere = $"Data Source={Path.Combine(_dir, "restored", "t.db")};Pooling=False";
        var restoredKeys = Path.Combine(_dir, "restored", "keys");
        var restored = await Run(elsewhere, "restore", Path.Combine(outDir, "tideline-20260401.db"), "--keys-tar", Path.Combine(outDir, "keys-20260401.tar"), "--keys-dir", restoredKeys, "--year", "2026");

        restored.Code.Should().Be(0, restored.Text);
        File.ReadAllText(Path.Combine(restoredKeys, "key-1.xml")).Should().Be("<key>secret</key>");

        // the same spread: the live database and the restored one add up the same year
        var live = BackupService.Verify(Path.Combine(outDir, "tideline-20260401.db"), Live, 2026);
        var back = BackupService.Verify(Path.Combine(_dir, "restored", "t.db"), Live, 2026);
        back.Backup.Should().Be(live.Backup);
        back.SameAsLive.Should().BeTrue();
        back.Backup.InCents.Should().BeGreaterThan(0);

        var again = await Run(elsewhere, "restore", Path.Combine(outDir, "tideline-20260401.db"));
        again.Code.Should().Be(1);
        again.Text.Should().Contain("already exists");
        (await Run(elsewhere, "restore", Path.Combine(outDir, "tideline-20260401.db"), "--force")).Code.Should().Be(0);
    }

    [Fact]
    public async Task A_damaged_copy_is_reported_not_trusted()
    {
        await LoadHousehold();
        var bad = Path.Combine(_dir, "damaged.db");
        await File.WriteAllTextAsync(bad, "this is not a database file at all, just some text that is long enough to look like one");

        var verify = await Run(Live, "backup", "--verify", bad);

        verify.Code.Should().Be(1);
        verify.Text.Should().Contain("damaged");

        var restore = await Run($"Data Source={Path.Combine(_dir, "r.db")};Pooling=False", "restore", bad);
        restore.Code.Should().Be(1);
        File.Exists(Path.Combine(_dir, "r.db")).Should().BeFalse("a bad copy is never put in place");
    }

    [Fact]
    public async Task The_export_has_every_table_and_nothing_that_signs_people_in()
    {
        await LoadHousehold();
        using var conn = new Tideline.Data.SqliteConnectionFactory(Live).Open();
        new Tideline.Data.Security.UserRepository(conn, new FixedClock(_now)).Create("owner@example.com", "Owner", Tideline.Core.Security.Role.Owner);

        using var zip = new MemoryStream();
        ExportService.Write(conn, zip);
        zip.Position = 0;
        using var archive = new System.IO.Compression.ZipArchive(zip);
        var names = archive.Entries.Select(e => e.FullName).ToList();

        names.Should().Contain(["transactions.csv", "accounts.csv", "rules.csv", "audit_log.csv", "users.csv"]);
        names.Should().NotContain(["sessions.csv", "api_tokens.csv", "user_mfa.csv", "recovery_codes.csv", "settings.csv", "simplefin_connections.csv", "notification_channels.csv"]);
        using var users = new StreamReader(archive.GetEntry("users.csv")!.Open());
        var text = await users.ReadToEndAsync();
        text.Should().Contain("owner@example.com").And.NotContain("password_hash");
        archive.GetEntry("transactions.csv")!.Length.Should().BeGreaterThan(500);
    }

    [Theory]
    [InlineData("plain", "plain")]
    [InlineData("a,b", "\"a,b\"")]
    [InlineData("say \"hi\"", "\"say \"\"hi\"\"\"")]
    [InlineData("=SUM(A1)", "'=SUM(A1)")]
    [InlineData("-123.45", "-123.45")]
    [InlineData("-evil", "'-evil")]
    public void Cells_are_quoted_and_cannot_run_as_formulas(string input, string expected) => ExportService.Cell(input).Should().Be(expected);
}
