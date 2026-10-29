using Dapper;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using Tideline.Core;
using Tideline.Core.Moves;
using Tideline.Data;
using Tideline.Data.Repositories;

namespace Tideline.Import.Tests;

public sealed class FixedClock(DateTimeOffset now) : TimeProvider
{
    public override DateTimeOffset GetUtcNow() => now;
}

/// <summary>
/// A small household built from the made-up fixtures in tests/fixtures/truist. The expected numbers are worked out by hand:
///   checking 1111, opening 1,000.00 : +2,000.00 payroll, -120.00 power, -500.00 check, -300.00 to savings,
///                                     -150.00 card payment, -10.99 Spotify, -20.00 mystery  = 1,899.01
///   savings  3333, opening 5,000.00 : +300.00 from checking, +1.25 interest                 = 5,301.25
///   card     2222                   : -85.40 groceries, +150.00 payment, -4.50 coffee       = +60.10
/// </summary>
public sealed class ImportPipelineTests : IDisposable
{
    private readonly string _dbPath = Path.Combine(Path.GetTempPath(), $"tideline-import-{Guid.NewGuid():N}.db");
    private readonly TimeProvider _clock = new FixedClock(new DateTimeOffset(2026, 4, 1, 9, 0, 0, TimeSpan.Zero));
    private readonly SqliteConnectionFactory _factory;
    private readonly ImportPipeline _pipeline;
    private long _checking, _savings, _card;

    public ImportPipelineTests()
    {
        var cs = $"Data Source={_dbPath};Pooling=False";
        Migrator.MigrateUp(cs);
        _factory = new SqliteConnectionFactory(cs);
        _pipeline = new ImportPipeline(_factory, new WriteGate(), _clock);
        SeedHousehold();
    }

    private void SeedHousehold()
    {
        using var c = _factory.Open();
        var accounts = new AccountRepository(c, _clock);
        _checking = accounts.Insert(new NewAccount("Checking", AccountKind.Checking, "Truist", "1111"));
        _savings = accounts.Insert(new NewAccount("Savings", AccountKind.Savings, "Truist", "3333"));
        _card = accounts.Insert(new NewAccount("Card", AccountKind.CreditCard, "Truist", "2222"));

        var cats = new CategoryRepository(c);
        var salary = cats.Insert("Salary", LineType.Income);
        var utilities = cats.Insert("Utilities", LineType.Bill);
        var subscriptions = cats.Insert("Subscriptions", LineType.Bill);
        var groceries = cats.Insert("Groceries", LineType.Required);
        var rent = cats.Insert("Rent", LineType.Required);

        var rules = new RuleRepository(c, _clock);
        rules.Insert(new NewRule("Income: payroll", 10,
            [("direction", "is", "\"in\""), ("account_kind", "in", "[\"checking\",\"savings\"]"), ("description", "contains_any", "[\"PAYROLL\"]")],
            SetCategoryId: salary, SetPayee: "Acme Corp"));
        rules.Insert(new NewRule("Bill: power", 20, [("description", "contains_any", "[\"CITY POWER\"]")], SetCategoryId: utilities, SetPayee: "City Power"));
        rules.Insert(new NewRule("Bill: Spotify", 30, [("description", "contains_any", "[\"SPOTIFY\"]")], SetCategoryId: subscriptions, SetPayee: "Spotify"));
        rules.Insert(new NewRule("Rent check", 40,
            [("description", "starts_with", "\"Check #\""), ("amount", "gte", "500"), ("amount", "lte", "500")], SetCategoryId: rent, SetPayee: "Landlord"));
        rules.Insert(new NewRule("Groceries", 50, [("description", "contains_any", "[\"GROCERY\"]")], SetCategoryId: groceries));

        var patterns = new MovePatternRepository(c);
        patterns.Insert(new MovePattern(0, _checking, "TRANSFER TO SAVINGS", _savings, "TRANSFER FROM CHECKING"));
        patterns.Insert(new MovePattern(0, _checking, "CARD PAYMENT", _card, "PAYMENT THANK YOU"));
    }

    private static string Fixture(string name) => File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "fixtures", "truist", name));

    private Task<ImportResult> Import(string name) => _pipeline.ImportTruistAsync(name, Fixture(name));

    private T Scalar<T>(string sql, object? args = null)
    {
        using var c = _factory.Open();
        return c.ExecuteScalar<T>(sql, args)!;
    }

    private async Task ImportAll()
    {
        await Import("acct_1111_03_01_2026_to_03_31_2026.csv");
        await Import("acct_3333_03_01_2026_to_03_31_2026.csv");
        await Import("acct_2222_03_01_2026_to_03_31_2026.csv");
    }

    [Fact]
    public async Task Checking_file_loads_with_the_expected_counts_balance_and_pending_moves()
    {
        var r = await Import("acct_1111_03_01_2026_to_03_31_2026.csv");

        r.Status.Should().Be(ImportStatus.Done);
        (r.RowsRead, r.RowsInserted, r.RowsExisting).Should().Be((7, 7, 0));
        r.MovesPaired.Should().Be(0);
        r.MovesPending.Should().Be(2, "the savings transfer and the card payment have no partner yet");
        r.RowsReview.Should().Be(1, "only MYSTERY VENDOR matches no rule");
        r.Reconciliation.Should().Be(new Reconciliation(_checking, new DateOnly(2026, 3, 9), 189901, 189901));
        r.Reconciliation!.Matches.Should().BeTrue();

        Scalar<long>("SELECT opening_balance_cents FROM accounts WHERE id = @id", new { id = _checking }).Should().Be(100000);
        Scalar<long>("SELECT balance_cents FROM balance_snapshots WHERE account_id = @id AND source = 'bank'", new { id = _checking }).Should().Be(189901);
    }

    [Fact]
    public async Task Rules_set_category_type_payee_and_the_catch_all_tags_review()
    {
        await Import("acct_1111_03_01_2026_to_03_31_2026.csv");

        using var c = _factory.Open();
        var rows = c.Query<(string Desc, string Payee, string Category, string Type, long? RuleId)>("""
            SELECT t.original_description, t.payee, g.name, t.type, t.rule_id
            FROM transactions t JOIN categories g ON g.id = t.category_id
            WHERE t.move_id IS NULL ORDER BY t.posted_on, t.id
            """).ToList();

        rows.Select(x => (x.Desc, x.Payee, x.Category, x.Type)).Should().Equal(
            ("PAYROLL ACME CORP", "Acme Corp", "Salary", "income"),
            ("CITY POWER AUTOPAY", "City Power", "Utilities", "bill"),
            ("Check #1042", "Landlord", "Rent", "required"),
            ("SPOTIFY USA", "Spotify", "Subscriptions", "bill"),
            ("MYSTERY VENDOR 9", "Mystery Vendor", "Uncategorized", "extra"));
        rows.Last().RuleId.Should().BeNull();

        c.Query<string>("SELECT g.name FROM transaction_tags tt JOIN tags g ON g.id = tt.tag_id JOIN transactions t ON t.id = tt.transaction_id WHERE t.original_description = 'MYSTERY VENDOR 9'")
            .Should().Equal("review");
    }

    [Fact]
    public async Task All_three_files_reconcile_and_every_move_pairs()
    {
        await Import("acct_1111_03_01_2026_to_03_31_2026.csv");
        var savings = await Import("acct_3333_03_01_2026_to_03_31_2026.csv");
        var card = await Import("acct_2222_03_01_2026_to_03_31_2026.csv");

        savings.MovesPaired.Should().Be(1);
        savings.Reconciliation!.BankCents.Should().Be(530125);
        savings.Reconciliation.Matches.Should().BeTrue();
        card.MovesPaired.Should().Be(1);
        card.MovesPending.Should().Be(0);

        Scalar<long>("SELECT COUNT(*) FROM transactions").Should().Be(12);
        Scalar<long>("SELECT COUNT(*) FROM moves").Should().Be(2);
        Scalar<long>("SELECT COUNT(*) FROM transactions WHERE move_id IS NOT NULL").Should().Be(4, "two complete moves of two legs");
        Scalar<long>("SELECT COUNT(*) FROM (SELECT move_id FROM transactions WHERE move_id IS NOT NULL GROUP BY move_id HAVING COUNT(*) <> 2)").Should().Be(0);

        using var c = _factory.Open();
        var ledger = new LedgerRepository(c, _clock);
        ledger.BalanceAsOf(_checking, new DateOnly(2026, 3, 31)).Should().Be(189901);
        ledger.BalanceAsOf(_savings, new DateOnly(2026, 3, 31)).Should().Be(530125);
        ledger.BalanceAsOf(_card, new DateOnly(2026, 3, 31)).Should().Be(6010);
    }

    [Fact]
    public async Task Moves_count_as_savings_only_when_one_leg_is_a_savings_account()
    {
        await ImportAll();

        var counts = Scalar<string>("""
            SELECT GROUP_CONCAT(counts_as) FROM (SELECT counts_as FROM moves ORDER BY counts_as)
            """);
        counts.Should().Be("none,savings", "card payment counts as nothing; checking to savings counts as savings");
    }

    [Fact]
    public async Task Spread_by_type_matches_the_hand_worked_totals_and_moves_carry_no_type()
    {
        await ImportAll();

        using var c = _factory.Open();
        var byType = c.Query<(string Type, long Total)>("""
            SELECT type, SUM(amount_cents) FROM transactions WHERE move_id IS NULL GROUP BY type ORDER BY type
            """).ToDictionary(x => x.Type, x => x.Total);

        byType.Should().BeEquivalentTo(new Dictionary<string, long>
        {
            ["income"] = 200125,                 // pay 2,000.00, and the unmatched deposit 1.25 (money in on checking with no rule is income)
            ["bill"] = -13099,                   // power 120.00 + Spotify 10.99
            ["required"] = -58540,               // rent check 500.00 + groceries 85.40
            ["extra"] = -2450,                   // mystery -20.00, coffee -4.50
        });
        Scalar<long>("SELECT COUNT(*) FROM transactions WHERE move_id IS NOT NULL AND type IS NOT NULL").Should().Be(0);
    }

    [Fact]
    public async Task Importing_the_same_file_again_adds_nothing()
    {
        await ImportAll();
        var before = Scalar<long>("SELECT COUNT(*) FROM transactions");

        var again = await Import("acct_1111_03_01_2026_to_03_31_2026.csv");

        again.Status.Should().Be(ImportStatus.Done);
        (again.RowsInserted, again.RowsExisting).Should().Be((0, 7));
        Scalar<long>("SELECT COUNT(*) FROM transactions").Should().Be(before);
    }

    [Fact]
    public async Task An_overlapping_export_adds_only_the_new_row()
    {
        await ImportAll();

        var r = await Import("acct_1111_03_09_2026_to_03_15_2026.csv");

        (r.RowsRead, r.RowsInserted, r.RowsExisting, r.RowsReview).Should().Be((3, 1, 2, 1));
        r.Reconciliation.Should().Be(new Reconciliation(_checking, new DateOnly(2026, 3, 10), 189551, 189551));
    }

    [Fact]
    public async Task An_older_file_after_a_newer_one_is_refused_because_it_would_sit_before_the_opening_balance()
    {
        await Import("acct_1111_03_09_2026_to_03_15_2026.csv");   // later days first: the opening balance is derived from this file

        var r = await Import("acct_1111_03_01_2026_to_03_31_2026.csv");

        r.Status.Should().Be(ImportStatus.Failed);
        r.Error.Should().Contain("Import files oldest first").And.Contain("2026-03-08");
        Scalar<long>("SELECT COUNT(*) FROM transactions").Should().Be(3);
    }

    [Fact]
    public async Task Oldest_first_ordering_puts_files_in_date_order_not_name_order()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"tideline-order-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        try
        {
            foreach (var n in new[] { "acct_1111_03_09_2026_to_03_15_2026.csv", "acct_1111_03_01_2026_to_03_31_2026.csv", "acct_2222_03_01_2026_to_03_31_2026.csv" })
                File.Copy(Path.Combine(AppContext.BaseDirectory, "fixtures", "truist", n), Path.Combine(dir, n));
            File.WriteAllText(Path.Combine(dir, "acct_9999_junk.csv"), "not a csv at all");

            var ordered = ImportOrder.OldestFirst(Directory.GetFiles(dir)).Select(Path.GetFileName).ToList();

            ordered.Should().Equal("acct_1111_03_01_2026_to_03_31_2026.csv", "acct_2222_03_01_2026_to_03_31_2026.csv",
                "acct_1111_03_09_2026_to_03_15_2026.csv", "acct_9999_junk.csv");
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public async Task A_file_with_a_missing_row_fails_whole_and_writes_nothing_but_the_failed_batch()
    {
        var r = await Import("acct_1111_missing_row.csv");

        r.Status.Should().Be(ImportStatus.Failed);
        r.Error.Should().Contain("3 day(s)").And.Contain("Export the file again");
        Scalar<long>("SELECT COUNT(*) FROM transactions").Should().Be(0);
        Scalar<string>("SELECT status FROM import_batches WHERE id = @id", new { id = r.BatchId }).Should().Be("failed");
        Scalar<long?>("SELECT opening_balance_cents FROM accounts WHERE id = @id", new { id = _checking }).Should().BeNull();
    }

    [Fact]
    public async Task Unknown_account_and_junk_files_fail_with_a_reason()
    {
        var unknown = await _pipeline.ImportTruistAsync("acct_9999_x.csv", Fixture("acct_2222_03_01_2026_to_03_31_2026.csv"));
        unknown.Status.Should().Be(ImportStatus.Failed);
        unknown.Error.Should().Contain("9999");

        var junk = await _pipeline.ImportTruistAsync("notes.txt", "hello");
        junk.Status.Should().Be(ImportStatus.Failed);
        junk.Error.Should().Contain("acct_1234");

        var wrongKind = await _pipeline.ImportTruistAsync("acct_1111_card.csv", Fixture("acct_2222_03_01_2026_to_03_31_2026.csv"));
        wrongKind.Status.Should().Be(ImportStatus.Failed);
        wrongKind.Error.Should().Contain("credit card export");

        Scalar<long>("SELECT COUNT(*) FROM transactions").Should().Be(0);
        Scalar<long>("SELECT COUNT(*) FROM import_batches WHERE status = 'failed'").Should().Be(3);
    }

    [Fact]
    public async Task Rows_after_the_csv_cutover_date_are_refused()
    {
        using (var c = _factory.Open())
            c.Execute("UPDATE accounts SET csv_until_on = '2026-03-04' WHERE id = @id", new { id = _checking });

        var r = await Import("acct_1111_03_01_2026_to_03_31_2026.csv");

        r.Status.Should().Be(ImportStatus.Failed);
        r.Error.Should().Contain("4 row(s)").And.Contain("2026-03-04").And.Contain("SimpleFIN");
        Scalar<long>("SELECT COUNT(*) FROM transactions").Should().Be(0);
    }

    [Fact]
    public async Task A_lone_transfer_leg_waits_and_pairs_when_its_partner_file_arrives_later()
    {
        await Import("acct_1111_03_01_2026_to_03_31_2026.csv");
        Scalar<long>("SELECT COUNT(*) FROM transactions WHERE move_id IS NOT NULL").Should().Be(2);

        await Import("acct_3333_03_01_2026_to_03_31_2026.csv");

        using var c = _factory.Open();
        new LedgerRepository(c, _clock).PendingMoveLegs().Should().ContainSingle("only the card payment is still waiting");
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        foreach (var f in new[] { _dbPath, _dbPath + "-wal", _dbPath + "-shm" })
            if (File.Exists(f)) File.Delete(f);
    }
}
