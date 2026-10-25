using FluentAssertions;
using Microsoft.Data.Sqlite;
using Tideline.Cli;

namespace Tideline.Cli.Tests;

public sealed class FixedClock(DateTimeOffset now) : TimeProvider
{
    public override DateTimeOffset GetUtcNow() => now;
}

public sealed class CliTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"tideline-cli-{Guid.NewGuid():N}");
    private readonly string _cs;

    public CliTests()
    {
        Directory.CreateDirectory(_dir);
        _cs = $"Data Source={Path.Combine(_dir, "t.db")};Pooling=False";
    }

    private static string Fixtures => Path.Combine(AppContext.BaseDirectory, "fixtures");

    private async Task<(int Code, string Text)> Run(params string[] args)
    {
        var sw = new StringWriter();
        var code = await new CliApp(sw, new FixedClock(new DateTimeOffset(2026, 4, 1, 9, 0, 0, TimeSpan.Zero)))
            .RunAsync([.. args, "--db", _cs]);
        return (code, sw.ToString());
    }

    private async Task Prepare()
    {
        (await Run("migrate")).Code.Should().Be(0);
        (await Run("setup", Path.Combine(Fixtures, "household.json"))).Code.Should().Be(0);
    }

    [Fact]
    public async Task Setup_is_safe_to_run_twice()
    {
        await Prepare();

        var again = await Run("setup", Path.Combine(Fixtures, "household.json"));

        again.Code.Should().Be(0);
        again.Text.Should().NotContain(": added").And.Contain("account Checking: already there").And.Contain("rule Groceries: already there");
    }

    [Fact]
    public async Task Importing_the_fixture_folder_reconciles_every_account()
    {
        await Prepare();

        var import = await Run("import", Path.Combine(Fixtures, "truist"));

        // the broken export and the overlapping export are in the same folder, so the run reports one failure
        import.Code.Should().Be(2);
        import.Text.Should().Contain("acct_1111_missing_row.csv: FAILED").And.Contain("Running balance does not match");
        import.Text.Should().Contain("balance matches the bank ($1,895.51)");

        var status = await Run("status");
        status.Text.Should().Contain("Checking").And.Contain("$1,895.51");
        status.Text.Should().Contain("Savings").And.Contain("$5,301.25");
        status.Text.Should().NotContain(" NO");
        status.Text.Should().Contain("Moves paired: 2. Waiting for a partner: 0.");
    }

    [Fact]
    public async Task Importing_one_good_file_prints_the_counts_and_returns_zero()
    {
        await Prepare();

        var r = await Run("import", Path.Combine(Fixtures, "truist", "acct_1111_03_01_2026_to_03_31_2026.csv"));

        r.Code.Should().Be(0);
        r.Text.Should().Contain("7 new, 0 already there, 1 to review").And.Contain("balance matches the bank ($1,899.01)");
        r.Text.Should().Contain("1 of 1 files imported");
    }

    [Fact]
    public async Task Lines_shows_what_the_ledger_made_of_each_row()
    {
        await Prepare();
        await Run("import", Path.Combine(Fixtures, "truist", "acct_1111_03_01_2026_to_03_31_2026.csv"));

        var all = await Run("lines", "--account", "1111");
        all.Text.Should().Contain("PAYROLL ACME CORP").And.Contain("income / Salary / Acme Corp");
        all.Text.Should().Contain("-$120.00").And.Contain("bill / Utilities / City Power");

        var two = await Run("lines", "--account", "1111", "--random", "2");
        two.Text.Split('\n', StringSplitOptions.RemoveEmptyEntries).Should().HaveCount(2);
    }

    [Fact]
    public async Task Bad_input_gives_a_message_not_a_stack_trace()
    {
        (await Run()).Code.Should().Be(1);
        var unknown = await Run("frobnicate");
        (unknown.Code, unknown.Text).Should().Match<(int, string)>(x => x.Item1 == 1 && x.Item2.Contains("unknown command"));

        var missing = await Run("import", Path.Combine(_dir, "nope.csv"));
        missing.Code.Should().Be(1);
        missing.Text.Should().Contain("is not a file or folder");

        File.WriteAllText(Path.Combine(_dir, "bad.json"), "{ \"accounts\": [ { \"name\": \"X\", \"kind\": \"boat\" } ] }");
        await Run("migrate");
        var badKind = await Run("setup", Path.Combine(_dir, "bad.json"));
        badKind.Code.Should().Be(1);
        badKind.Text.Should().Contain("unknown kind 'boat'");
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true);
    }
}
