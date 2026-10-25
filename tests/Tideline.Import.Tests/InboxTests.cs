using Dapper;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using Tideline.Core;
using Tideline.Data;
using Tideline.Data.Repositories;

namespace Tideline.Import.Tests;

public sealed class InboxTests : IDisposable
{
    private static readonly DateTimeOffset Now = new(2026, 4, 1, 9, 0, 0, TimeSpan.Zero);

    private readonly string _root = Path.Combine(Path.GetTempPath(), $"tideline-inbox-{Guid.NewGuid():N}");
    private readonly string _inbox;
    private readonly SqliteConnectionFactory _factory;
    private readonly InboxProcessor _inboxProcessor;

    public InboxTests()
    {
        _inbox = Path.Combine(_root, "inbox");
        Directory.CreateDirectory(_inbox);
        var cs = $"Data Source={Path.Combine(_root, "t.db")};Pooling=False";
        Migrator.MigrateUp(cs);
        _factory = new SqliteConnectionFactory(cs);

        TimeProvider clock = new FixedClock(Now);
        using (var c = _factory.Open())
        {
            var accounts = new AccountRepository(c, clock);
            accounts.Insert(new NewAccount("Checking", AccountKind.Checking, "Truist", "1111"));
            accounts.Insert(new NewAccount("Savings", AccountKind.Savings, "Truist", "3333"));
            accounts.Insert(new NewAccount("Card", AccountKind.CreditCard, "Truist", "2222"));
        }
        _inboxProcessor = new InboxProcessor(_inbox, new ImportPipeline(_factory, new WriteGate(), clock), _factory, clock);
    }

    private static string FixtureText(string name) =>
        File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "fixtures", "truist", name));

    private void Drop(string name, string? contents = null, TimeSpan? age = null)
    {
        var path = Path.Combine(_inbox, name);
        File.WriteAllText(path, contents ?? FixtureText(name));
        File.SetLastWriteTimeUtc(path, (Now - (age ?? TimeSpan.FromMinutes(10))).UtcDateTime);
    }

    private long Scalar(string sql)
    {
        using var c = _factory.Open();
        return c.ExecuteScalar<long>(sql);
    }

    private string?[] Names(string sub) =>
        Directory.Exists(Path.Combine(_inbox, sub))
            ? Directory.EnumerateFiles(Path.Combine(_inbox, sub), "*", SearchOption.AllDirectories).Select(Path.GetFileName).OrderBy(x => x).ToArray()
            : [];

    [Fact]
    public async Task Good_files_go_to_Done_by_month_and_are_imported()
    {
        Drop("acct_1111_03_01_2026_to_03_31_2026.csv");

        var run = await _inboxProcessor.RunOnceAsync();

        run.Should().Be(new InboxRunSummary(1, 0, 0));
        File.Exists(Path.Combine(_inbox, "Done", "2026-04", "acct_1111_03_01_2026_to_03_31_2026.csv")).Should().BeTrue();
        Directory.EnumerateFiles(_inbox).Should().BeEmpty();
        Scalar("SELECT COUNT(*) FROM transactions").Should().Be(7);
        Scalar("SELECT COUNT(*) FROM system_events WHERE level = 'info' AND source = 'inbox'").Should().Be(1);
    }

    [Fact]
    public async Task A_junk_txt_goes_to_Failed_with_a_reason_beside_it()
    {
        Drop("notes.txt", "remember the milk");

        var run = await _inboxProcessor.RunOnceAsync();

        run.Should().Be(new InboxRunSummary(0, 1, 0));
        Names("Failed").Should().Equal("notes.txt", "notes.txt.error.txt");
        File.ReadAllText(Path.Combine(_inbox, "Failed", "notes.txt.error.txt")).Should().Contain("notes.txt is not a CSV file.");
        Scalar("SELECT COUNT(*) FROM system_events WHERE level = 'error'").Should().Be(1);
    }

    [Fact]
    public async Task Each_failure_reason_is_written_for_a_person()
    {
        Drop("acct_9999_x.csv", FixtureText("acct_2222_03_01_2026_to_03_31_2026.csv"));
        Drop("acct_1111_missing_row.csv");
        Drop("acct_1111_empty.csv", "Posted Date,Transaction Date,Transaction Type,Check/Serial #,Description,Amount\n");
        Drop("statement.csv", "a,b\n1,2\n");

        await _inboxProcessor.RunOnceAsync();

        string Reason(string f) => File.ReadAllText(Path.Combine(_inbox, "Failed", f + ".error.txt"));
        Reason("acct_9999_x.csv").Should().Contain("No account has the bank number 9999");
        Reason("acct_1111_missing_row.csv").Should().Contain("Running balance does not match").And.Contain("3 day(s)");
        Reason("acct_1111_empty.csv").Should().Contain("has no rows");
        Reason("statement.csv").Should().Contain("expected a name like acct_1234");
        Scalar("SELECT COUNT(*) FROM transactions").Should().Be(0);
    }

    [Fact]
    public async Task A_file_dropped_again_adds_nothing_and_its_copy_is_kept_with_a_timestamp()
    {
        Drop("acct_1111_03_01_2026_to_03_31_2026.csv");
        await _inboxProcessor.RunOnceAsync();
        Drop("acct_1111_03_01_2026_to_03_31_2026.csv");

        var run = await _inboxProcessor.RunOnceAsync();

        run.Imported.Should().Be(1);
        Scalar("SELECT COUNT(*) FROM transactions").Should().Be(7, "the re-dropped file adds 0");
        Names("Done").Should().HaveCount(2);
        Names("Done").Should().Contain(n => n!.Contains("-20260401090000"));
    }

    [Fact]
    public async Task Files_still_being_copied_and_dot_files_and_folders_are_left_alone()
    {
        Drop("acct_1111_03_01_2026_to_03_31_2026.csv", age: TimeSpan.FromSeconds(20));
        Drop(".hidden.csv", "x");
        Directory.CreateDirectory(Path.Combine(_inbox, "subfolder"));

        var run = await _inboxProcessor.RunOnceAsync();

        run.Should().Be(new InboxRunSummary(0, 0, 1));
        var path = Path.Combine(_inbox, "acct_1111_03_01_2026_to_03_31_2026.csv");
        File.Exists(path).Should().BeTrue();
        File.Exists(Path.Combine(_inbox, ".hidden.csv")).Should().BeTrue();
        Scalar("SELECT COUNT(*) FROM transactions").Should().Be(0);

        // once it has been quiet for a minute the same file is picked up
        File.SetLastWriteTimeUtc(path, (Now - TimeSpan.FromMinutes(2)).UtcDateTime);
        (await _inboxProcessor.RunOnceAsync()).Imported.Should().Be(1);
    }

    [Fact]
    public async Task If_the_database_is_unreachable_the_file_stays_in_the_inbox()
    {
        Drop("acct_1111_03_01_2026_to_03_31_2026.csv");
        SqliteConnection.ClearAllPools();
        var broken = new SqliteConnectionFactory($"Data Source={Path.Combine(_root, "no-such-dir", "t.db")};Pooling=False");
        TimeProvider clock = new FixedClock(Now);
        var processor = new InboxProcessor(_inbox, new ImportPipeline(broken, new WriteGate(), clock), broken, clock);

        var run = await processor.RunOnceAsync();

        run.Should().Be(new InboxRunSummary(0, 0, 0));
        File.Exists(Path.Combine(_inbox, "acct_1111_03_01_2026_to_03_31_2026.csv")).Should().BeTrue();
        Directory.Exists(Path.Combine(_inbox, "Done")).Should().BeFalse();
        Directory.Exists(Path.Combine(_inbox, "Failed")).Should().BeFalse();
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }
}
