using FluentAssertions;
using Microsoft.Data.Sqlite;
using Tideline.Cli;
using Tideline.Data;
using Tideline.Import;

namespace Tideline.Cli.Tests;

public sealed class DemoDataTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"tideline-demo-{Guid.NewGuid():N}");

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true);
    }

    private string Folder(string name) => Path.Combine(_dir, name);

    [Fact]
    public void Generates_exactly_the_lines_asked_for_across_six_accounts()
    {
        var r = DemoData.Generate(Folder("a"), DemoOptions.Default);

        r.Lines.Should().Be(5000);
        r.LinesPerAccount.Should().HaveCount(6).And.OnlyContain(kv => kv.Value > 0);
        r.LinesPerAccount.Values.Sum().Should().Be(5000);
        r.Files.Should().Be(Directory.GetFiles(Folder("a"), "acct_*.csv").Length);
        File.Exists(r.HouseholdFile).Should().BeTrue();
    }

    [Theory]
    [InlineData(1500)]
    [InlineData(2222)]
    [InlineData(8000)]
    public void Any_reasonable_line_count_is_hit_exactly(int lines) =>
        DemoData.Generate(Folder($"n{lines}"), DemoOptions.Default with { Lines = lines }).Lines.Should().Be(lines);

    [Fact]
    public void Too_few_lines_is_refused_with_the_minimum()
    {
        FluentActions.Invoking(() => DemoData.Generate(Folder("few"), DemoOptions.Default with { Lines = 100 }))
            .Should().Throw<ArgumentException>().WithMessage("*too few*Ask for at least*");
    }

    [Fact]
    public void The_same_seed_gives_identical_files_and_a_different_seed_does_not()
    {
        DemoData.Generate(Folder("one"), DemoOptions.Default with { Lines = 1800 });
        DemoData.Generate(Folder("two"), DemoOptions.Default with { Lines = 1800 });
        DemoData.Generate(Folder("other"), DemoOptions.Default with { Lines = 1800, Seed = 7 });

        string Digest(string f) => string.Join("|", Directory.GetFiles(Folder(f)).OrderBy(x => x).Select(x => Path.GetFileName(x) + File.ReadAllText(x)));
        Digest("one").Should().Be(Digest("two"));
        Digest("other").Should().NotBe(Digest("one"));
    }

    [Fact]
    public void A_shorter_period_works_too()
    {
        var r = DemoData.Generate(Folder("short"), DemoOptions.Default with { Lines = 1000, From = new DateOnly(2026, 1, 1), To = new DateOnly(2026, 3, 31) });

        r.Lines.Should().Be(1000);
        r.Files.Should().Be(18, "three months of six accounts");
    }

    [Fact]
    public async Task Every_generated_file_imports_every_deposit_account_matches_its_bank_and_every_transfer_pairs()
    {
        var cs = $"Data Source={Path.Combine(_dir, "t.db")};Pooling=False";
        Directory.CreateDirectory(_dir);
        var sw = new StringWriter();
        var app = new CliApp(sw, TimeProvider.System);

        var code = await app.RunAsync(["demo", Folder("load"), "--load", "--db", cs]);
        var text = sw.ToString();

        code.Should().Be(0, text.Length > 2000 ? text[^2000..] : text);
        text.Should().Contain("126 of 126 files imported, 5000 new lines");

        var status = new StringWriter();
        await new CliApp(status, TimeProvider.System).RunAsync(["status", "--db", cs]);
        var lines = status.ToString().Split('\n');

        foreach (var name in new[] { "Everyday Checking", "Bills Checking", "Money Market", "Emergency Fund" })
            lines.Single(l => l.StartsWith(name)).TrimEnd().Should().EndWith("yes", $"{name} must match the bank to the cent");
        status.ToString().Should().Contain("Moves paired: 103. Waiting for a partner: 0.");
    }

    [Fact]
    public async Task Importing_the_demo_files_twice_adds_nothing_the_second_time()
    {
        var cs = $"Data Source={Path.Combine(_dir, "t.db")};Pooling=False";
        Directory.CreateDirectory(_dir);
        DemoData.Generate(Folder("twice"), DemoOptions.Default with { Lines = 1600 });
        Migrator.MigrateUp(cs);
        var factory = new SqliteConnectionFactory(cs);
        var clock = TimeProvider.System;
        HouseholdSetup.Apply(HouseholdSetup.Parse(File.ReadAllText(Path.Combine(Folder("twice"), "household.json"))), factory, clock);
        var pipeline = new ImportPipeline(factory, new WriteGate(), clock);

        int Inserted() => ImportOrder.OldestFirst(Directory.GetFiles(Folder("twice"), "acct_*.csv"))
            .Sum(p => pipeline.ImportTruistAsync(Path.GetFileName(p), File.ReadAllText(p)).GetAwaiter().GetResult().RowsInserted);

        Inserted().Should().Be(1600);
        Inserted().Should().Be(0);
        await Task.CompletedTask;
    }
}
