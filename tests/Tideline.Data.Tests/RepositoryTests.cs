using FluentAssertions;
using Microsoft.Data.Sqlite;
using Tideline.Core;
using Tideline.Core.Moves;
using Tideline.Core.Rules;
using Tideline.Data.Repositories;

namespace Tideline.Data.Tests;

public sealed class FixedClock(DateTimeOffset now) : TimeProvider
{
    public override DateTimeOffset GetUtcNow() => now;
}

public abstract class RepositoryTestBase : IDisposable
{
    private readonly SqliteTestDb _db = SqliteTestDb.Migrated();
    protected readonly SqliteConnection Conn;
    protected readonly TimeProvider Clock = new FixedClock(new DateTimeOffset(2026, 3, 15, 12, 0, 0, TimeSpan.Zero));

    protected AccountRepository Accounts => new(Conn, Clock);
    protected CategoryRepository Categories => new(Conn);
    protected TagRepository Tags => new(Conn);
    protected RuleRepository Rules => new(Conn, Clock);
    protected MovePatternRepository Patterns => new(Conn);
    protected LedgerRepository Ledger => new(Conn, Clock);

    protected RepositoryTestBase()
    {
        Conn = new SqliteConnectionFactory(_db.ConnectionString).Open();
    }

    protected long NewChecking(string name = "Checking", string bank = "1111") =>
        Accounts.Insert(new NewAccount(name, AccountKind.Checking, "Truist", bank));

    protected NewTransaction Tx(long account, string externalId, long cents = -1000, int day = 2, LineType? type = LineType.Extra,
        long? categoryId = null, long? moveId = null) =>
        new(account, new DateOnly(2026, 3, day), cents, "COFFEE SHOP", externalId, ImportSource.Csv,
            Type: moveId is null ? type : null, CategoryId: categoryId, MoveId: moveId);

    public void Dispose()
    {
        Conn.Dispose();
        _db.Dispose();
    }
}

public class AccountRepositoryTests : RepositoryTestBase
{
    [Fact]
    public void Round_trips_every_field_including_enums_dates_and_booleans()
    {
        var id = Accounts.Insert(new NewAccount("Everyday", AccountKind.CreditCard, "Truist", "2222", 12345, new DateOnly(2026, 1, 1),
            IsInSpread: false, CsvUntilOn: new DateOnly(2026, 6, 30)));

        var a = Accounts.Get(id)!;

        a.Should().Be(new Account(id, "Everyday", AccountKind.CreditCard, "Truist", "2222", 12345, new DateOnly(2026, 1, 1),
            false, false, new DateOnly(2026, 6, 30), null));
    }

    [Fact]
    public void Looks_accounts_up_by_bank_number_and_updates_the_opening_balance()
    {
        var id = NewChecking(bank: "1111");
        NewChecking("Savings", "3333");

        Accounts.ByBankNumber("1111").Should().ContainSingle().Which.Id.Should().Be(id);
        Accounts.ByBankNumber("9999").Should().BeEmpty();

        Accounts.SetOpening(id, 250000, new DateOnly(2026, 2, 28));
        Accounts.Get(id)!.Should().Match<Account>(a => a.OpeningBalanceCents == 250000 && a.OpeningOn == new DateOnly(2026, 2, 28));
    }

    [Fact]
    public void Same_institution_and_bank_number_is_refused() =>
        FluentActions.Invoking(() => { NewChecking("A", "1111"); NewChecking("B", "1111"); })
            .Should().Throw<SqliteException>();

    [Fact]
    public void Unknown_kind_is_refused_by_the_check_constraint() =>
        FluentActions.Invoking(() => Conn.Execute("INSERT INTO accounts (name, kind, created_at) VALUES ('x', 'boat', '2026-01-01T00:00:00Z')"))
            .Should().Throw<SqliteException>();
}

file static class ConnExt
{
    public static void Execute(this SqliteConnection c, string sql) => Dapper.SqlMapper.Execute(c, sql);
}

public class CategoryAndTagRepositoryTests : RepositoryTestBase
{
    [Fact]
    public void Categories_keep_their_default_type()
    {
        var groceries = Categories.Insert("Groceries", LineType.Required);
        var fun = Categories.Insert("Fun", LineType.OneTime);

        Categories.DefaultTypes().Should().BeEquivalentTo(new Dictionary<long, LineType> { [groceries] = LineType.Required, [fun] = LineType.OneTime });
        Categories.IdByName("Groceries").Should().Be(groceries);
        Categories.IdByName("Nope").Should().BeNull();
    }

    [Fact]
    public void Tags_are_created_once()
    {
        var a = Tags.GetOrCreate("review");
        Tags.GetOrCreate("review").Should().Be(a);
        Tags.GetOrCreate("kids").Should().NotBe(a);
    }
}

public class RuleRepositoryTests : RepositoryTestBase
{
    [Fact]
    public void Rules_come_back_in_priority_order_with_conditions_and_tags_ready_for_the_engine()
    {
        var gas = Categories.Insert("Gas", LineType.Required);
        var groceries = Categories.Insert("Groceries", LineType.Required);
        var review = Tags.GetOrCreate("review");

        Rules.Insert(new NewRule("Groceries", 50, [("description", "contains_any", "[\"STORE\"]")], SetCategoryId: groceries));
        Rules.Insert(new NewRule("Store fuel", 10,
            [("description", "contains_any", "[\"STORE FUEL\"]"), ("amount", "gte", "5")],
            SetCategoryId: gas, SetType: LineType.OneTime, SetPayee: "Store Fuel", TagIds: [review]));

        var rules = Rules.All();

        rules.Select(r => r.Name).Should().Equal("Store fuel", "Groceries");
        rules[0].Conditions.Should().BeEquivalentTo(new RuleCondition[] { new DescriptionContainsAny(["STORE FUEL"]), new AmountAtLeast(500) },
            o => o.RespectingRuntimeTypes().WithStrictOrdering());
        (rules[0].SetType, rules[0].SetPayee).Should().Be((LineType.OneTime, "Store Fuel"));
        rules[0].TagIds.Should().Equal(review);

        var line = new RuleLine("STORE FUEL 77", -4000, "1111", AccountKind.Checking);
        RulesEngine.Decide(line, rules, Categories.DefaultTypes().GetValueOrDefault)!.CategoryId.Should().Be(gas);
    }

    [Fact]
    public void Duplicate_rule_names_are_refused() =>
        FluentActions.Invoking(() =>
        {
            Rules.Insert(new NewRule("Same", 10, []));
            Rules.Insert(new NewRule("Same", 20, []));
        }).Should().Throw<SqliteException>();
}

public class LedgerRepositoryTests : RepositoryTestBase
{
    [Fact]
    public void Insert_stores_the_line_with_tags_and_reads_it_back()
    {
        var acct = NewChecking();
        var cat = Categories.Insert("Fun", LineType.Extra);
        var tag = Tags.GetOrCreate("review");

        var id = Ledger.Insert(Tx(acct, "truist:a", -1234, categoryId: cat) with { Payee = "Coffee", TagIds = [tag, tag] });

        var row = Ledger.Get(id)!;
        row.Should().Be(new TransactionRow(id, acct, new DateOnly(2026, 3, 2), -1234, "COFFEE SHOP", "truist:a", "Coffee", cat, LineType.Extra, null, null, false));
        Tags.NamesFor(id).Should().Equal("review");
    }

    [Fact]
    public void External_ids_are_unique_and_existing_ones_can_be_found()
    {
        var acct = NewChecking();
        Ledger.Insert(Tx(acct, "truist:a"));
        Ledger.Insert(Tx(acct, "truist:b"));

        Ledger.ExistingExternalIds(["truist:a", "truist:zzz", "truist:b"]).Should().BeEquivalentTo("truist:a", "truist:b");
        FluentActions.Invoking(() => Ledger.Insert(Tx(acct, "truist:a"))).Should().Throw<SqliteException>();
    }

    [Fact]
    public void Many_ids_are_looked_up_in_chunks()
    {
        var acct = NewChecking();
        var ids = Enumerable.Range(0, 1200).Select(i => $"truist:{i:D5}").ToList();
        foreach (var id in ids.Take(1100)) Ledger.Insert(Tx(acct, id));

        Ledger.ExistingExternalIds(ids).Should().HaveCount(1100);
    }

    [Fact]
    public void A_line_needs_a_type_or_a_move()
    {
        var acct = NewChecking();
        FluentActions.Invoking(() => Ledger.Insert(Tx(acct, "truist:a", type: null))).Should().Throw<SqliteException>();
    }

    [Fact]
    public void A_lone_leg_is_pending_until_its_partner_joins_the_same_move()
    {
        var a = NewChecking("A", "1111");
        var b = NewChecking("B", "2222");

        var move = Ledger.CreateMove(MoveCountsAs.None);
        var first = Ledger.Insert(Tx(a, "truist:out", -5000, moveId: move));

        Ledger.PendingMoveLegs().Should().ContainSingle()
            .Which.Should().Be(new PendingLeg(first, a, new DateOnly(2026, 3, 2), -5000, "COFFEE SHOP", move));

        Ledger.Insert(Tx(b, "truist:in", 5000, moveId: move));
        Ledger.PendingMoveLegs().Should().BeEmpty();
    }

    [Fact]
    public void Balance_is_opening_plus_rows_up_to_the_date()
    {
        var acct = Accounts.Insert(new NewAccount("Checking", AccountKind.Checking, "Truist", "1111", OpeningBalanceCents: 100000));
        Ledger.Insert(Tx(acct, "truist:a", -2500, day: 2));
        Ledger.Insert(Tx(acct, "truist:b", 1000, day: 5));

        Ledger.BalanceAsOf(acct, new DateOnly(2026, 3, 1)).Should().Be(100000);
        Ledger.BalanceAsOf(acct, new DateOnly(2026, 3, 2)).Should().Be(97500);
        Ledger.BalanceAsOf(acct, new DateOnly(2026, 3, 31)).Should().Be(98500);

        var noOpening = NewChecking("Other", "9999");
        Ledger.BalanceAsOf(noOpening, new DateOnly(2026, 3, 31)).Should().Be(0);
    }

    [Fact]
    public void Imported_rows_are_protected_by_the_database_even_from_the_repository_connection()
    {
        var acct = NewChecking();
        var id = Ledger.Insert(Tx(acct, "truist:a"));

        FluentActions.Invoking(() => Dapper.SqlMapper.Execute(Conn, "UPDATE transactions SET amount_cents = 1 WHERE id = @id", new { id }))
            .Should().Throw<SqliteException>().WithMessage("*read-only*");
        FluentActions.Invoking(() => Dapper.SqlMapper.Execute(Conn, "DELETE FROM transactions WHERE id = @id", new { id }))
            .Should().Throw<SqliteException>().WithMessage("*cannot be deleted*");
    }
}

public class PatternSnapshotAndBatchTests : RepositoryTestBase
{
    [Fact]
    public void Move_patterns_round_trip()
    {
        var a = NewChecking("A", "1111");
        var b = NewChecking("B", "2222");
        var id = Patterns.Insert(new MovePattern(0, a, "TRANSFER TO", b, "TRANSFER FROM", 3));

        Patterns.All().Should().Equal(new MovePattern(id, a, "TRANSFER TO", b, "TRANSFER FROM", 3));
    }

    [Fact]
    public void A_bank_snapshot_is_replaced_for_the_same_day_and_latest_wins()
    {
        var acct = NewChecking();
        var snaps = new BalanceSnapshotRepository(Conn, Clock);

        snaps.UpsertBank(acct, new DateOnly(2026, 3, 1), 100);
        snaps.UpsertBank(acct, new DateOnly(2026, 3, 1), 150);
        snaps.UpsertBank(acct, new DateOnly(2026, 3, 9), 175);

        snaps.LatestBank(acct).Should().Be(175);
        Dapper.SqlMapper.ExecuteScalar<long>(Conn, "SELECT COUNT(*) FROM balance_snapshots").Should().Be(2);
    }

    [Fact]
    public void An_import_batch_is_started_then_finished_with_counts()
    {
        var acct = NewChecking();
        var batches = new ImportBatchRepository(Conn, Clock);

        var id = batches.Start(ImportSource.Csv, "acct_1111_x.csv", "abc", acct);
        batches.Get(id)!.Status.Should().Be("running");

        batches.Finish(id, "done", 10, 7, 3, 2);
        batches.Get(id).Should().Be(new ImportBatchSummary(id, "done", 10, 7, 3, 2, null));
    }
}
