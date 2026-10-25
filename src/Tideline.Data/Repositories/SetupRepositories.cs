using System.Data;
using Dapper;
using Tideline.Data.Security;
using Tideline.Core;
using Tideline.Core.Moves;
using Tideline.Core.Rules;
using Rule = Tideline.Core.Rules.Rule;

namespace Tideline.Data.Repositories;

// Rows are read into small mutable classes: Dapper's property mapping converts SQLite's 64-bit integers
// and text, whereas constructor mapping demands exact types.

internal static class Sql
{
    // The one place that knows how SQLite hands back a new row id (a dialect seam for a later MariaDB).
    public const string LastInsertId = "SELECT last_insert_rowid()";
}

public sealed class AccountRepository(IDbConnection conn, TimeProvider clock, IDbTransaction? tx = null)
{
    private const string Select = """
        SELECT id AS Id, name AS Name, kind AS Kind, institution AS Institution, bank_number_hash AS BankNumberHash,
               opening_balance_cents AS OpeningBalanceCents, opening_on AS OpeningOn, is_in_spread AS IsInSpread,
               is_closed AS IsClosed, csv_until_on AS CsvUntilOn, simplefin_from_on AS SimplefinFromOn
        FROM accounts
        """;

    private sealed class Row
    {
        public long Id { get; set; }
        public string Name { get; set; } = "";
        public string Kind { get; set; } = "";
        public string? Institution { get; set; }
        public string? BankNumberHash { get; set; }
        public long? OpeningBalanceCents { get; set; }
        public DateOnly? OpeningOn { get; set; }
        public bool IsInSpread { get; set; }
        public bool IsClosed { get; set; }
        public DateOnly? CsvUntilOn { get; set; }
        public DateOnly? SimplefinFromOn { get; set; }

        public Account ToAccount() => new(Id, Name, TypeHandlers.FromDb<AccountKind>(Kind), Institution, BankNumberHash,
            OpeningBalanceCents, OpeningOn, IsInSpread, IsClosed, CsvUntilOn, SimplefinFromOn);
    }

    public long Insert(NewAccount a)
    {
        TypeHandlers.EnsureRegistered();
        var last = AccountNumbers.LastFour(a.BankNumber);
        if (a.BankNumber is { Length: > 0 } && last is null) throw new ArgumentException("An account number needs at least its last 4 digits.");
        conn.Execute("""
            INSERT INTO accounts (name, kind, institution, bank_number_hash, bank_number_enc, opening_balance_cents, opening_on, is_in_spread,
                                  csv_until_on, simplefin_from_on, created_at)
            VALUES (@Name, @Kind, @Institution, @BankHash, @BankEnc, @OpeningBalanceCents, @OpeningOn, @IsInSpread,
                    @CsvUntilOn, @SimplefinFromOn, @Now)
            """, new
        {
            a.Name, Kind = a.Kind.ToDb(), a.Institution, BankHash = last is null ? null : AccountNumbers.Keys.Hash(last), BankEnc = last is null ? null : AccountNumbers.Keys.Seal(last), a.OpeningBalanceCents, a.OpeningOn,
            IsInSpread = a.IsInSpread ? 1 : 0, a.CsvUntilOn, a.SimplefinFromOn, Now = clock.GetUtcNow(),
        }, tx);
        return conn.ExecuteScalar<long>(Sql.LastInsertId, transaction: tx);
    }

    public Account? Get(long id)
    {
        TypeHandlers.EnsureRegistered();
        return conn.QuerySingleOrDefault<Row>($"{Select} WHERE id = @id", new { id }, tx)?.ToAccount();
    }

    public IReadOnlyList<Account> All()
    {
        TypeHandlers.EnsureRegistered();
        return conn.Query<Row>($"{Select} ORDER BY sort_order, id", transaction: tx).Select(r => r.ToAccount()).ToList();
    }

    /// <summary>The account a Truist file belongs to (from <c>acct_NNNN_</c> in its name). The digits are turned into the keyed hash; they are never compared as text.</summary>
    public IReadOnlyList<Account> ByBankNumber(string bankNumber)
    {
        TypeHandlers.EnsureRegistered();
        var hash = AccountNumbers.HashOf(bankNumber);
        if (hash is null) return [];
        return conn.Query<Row>($"{Select} WHERE bank_number_hash = @hash", new { hash }, tx).Select(r => r.ToAccount()).ToList();
    }

    public void SetOpening(long accountId, long balanceCents, DateOnly openingOn)
    {
        TypeHandlers.EnsureRegistered();
        conn.Execute("UPDATE accounts SET opening_balance_cents = @balanceCents, opening_on = @openingOn WHERE id = @accountId",
            new { accountId, balanceCents, openingOn }, tx);
    }
}

public sealed class CategoryRepository(IDbConnection conn, IDbTransaction? tx = null)
{
    private sealed class Row
    {
        public long Id { get; set; }
        public string Name { get; set; } = "";
        public string DefaultType { get; set; } = "";
        public bool IsArchived { get; set; }
    }

    public long Insert(string name, LineType defaultType)
    {
        conn.Execute("INSERT INTO categories (name, default_type) VALUES (@name, @type)", new { name, type = defaultType.ToDb() }, tx);
        return conn.ExecuteScalar<long>(Sql.LastInsertId, transaction: tx);
    }

    public IReadOnlyList<Category> All() =>
        conn.Query<Row>("SELECT id AS Id, name AS Name, default_type AS DefaultType, is_archived AS IsArchived FROM categories ORDER BY sort_order, id", transaction: tx)
            .Select(r => new Category(r.Id, r.Name, TypeHandlers.FromDb<LineType>(r.DefaultType), r.IsArchived)).ToList();

    public long? IdByName(string name) =>
        conn.ExecuteScalar<long?>("SELECT id FROM categories WHERE name = @name", new { name }, tx);

    public IReadOnlyDictionary<long, LineType> DefaultTypes() => All().ToDictionary(c => c.Id, c => c.DefaultType);
}

public sealed class TagRepository(IDbConnection conn, IDbTransaction? tx = null)
{
    public long GetOrCreate(string name)
    {
        var id = conn.ExecuteScalar<long?>("SELECT id FROM tags WHERE name = @name", new { name }, tx);
        if (id is not null) return id.Value;
        conn.Execute("INSERT INTO tags (name) VALUES (@name)", new { name }, tx);
        return conn.ExecuteScalar<long>(Sql.LastInsertId, transaction: tx);
    }

    public IReadOnlyList<string> NamesFor(long transactionId) =>
        conn.Query<string>("""
            SELECT g.name FROM transaction_tags tt JOIN tags g ON g.id = tt.tag_id
            WHERE tt.transaction_id = @transactionId ORDER BY g.name
            """, new { transactionId }, tx).ToList();
}

public sealed class RuleRepository(IDbConnection conn, TimeProvider clock, IDbTransaction? tx = null)
{
    public long Insert(NewRule r)
    {
        var now = clock.GetUtcNow();
        conn.Execute("""
            INSERT INTO rules (name, priority, is_active, set_category_id, set_type, set_payee, set_recurring_id, set_plan_id, created_at, updated_at)
            VALUES (@Name, @Priority, @IsActive, @SetCategoryId, @SetType, @SetPayee, @SetRecurringId, @SetPlanId, @now, @now)
            """, new
        {
            r.Name, r.Priority, IsActive = r.IsActive ? 1 : 0, r.SetCategoryId, SetType = r.SetType?.ToDb(), r.SetPayee,
            r.SetRecurringId, r.SetPlanId, now,
        }, tx);
        var id = conn.ExecuteScalar<long>(Sql.LastInsertId, transaction: tx);

        foreach (var (field, op, valueJson) in r.Conditions)
            conn.Execute("INSERT INTO rule_conditions (rule_id, field, op, value) VALUES (@id, @field, @op, @valueJson)",
                new { id, field, op, valueJson }, tx);
        foreach (var tagId in r.TagIds ?? [])
            conn.Execute("INSERT INTO rule_tags (rule_id, tag_id) VALUES (@id, @tagId)", new { id, tagId }, tx);
        return id;
    }

    private sealed class RuleRow
    {
        public long Id { get; set; }
        public string Name { get; set; } = "";
        public int Priority { get; set; }
        public bool IsActive { get; set; }
        public long? SetCategoryId { get; set; }
        public string? SetType { get; set; }
        public string? SetPayee { get; set; }
        public long? SetRecurringId { get; set; }
        public long? SetPlanId { get; set; }
    }

    private sealed class ConditionRow
    {
        public long RuleId { get; set; }
        public string Field { get; set; } = "";
        public string Op { get; set; } = "";
        public string Value { get; set; } = "";
    }

    private sealed class TagRow
    {
        public long RuleId { get; set; }
        public long TagId { get; set; }
    }

    /// <summary>All rules in evaluation order (the engine skips inactive ones itself).</summary>
    public IReadOnlyList<Rule> All()
    {
        var rules = conn.Query<RuleRow>("""
            SELECT id AS Id, name AS Name, priority AS Priority, is_active AS IsActive, set_category_id AS SetCategoryId,
                   set_type AS SetType, set_payee AS SetPayee, set_recurring_id AS SetRecurringId, set_plan_id AS SetPlanId
            FROM rules ORDER BY priority, id
            """, transaction: tx).ToList();
        var conditions = conn.Query<ConditionRow>("SELECT rule_id AS RuleId, field AS Field, op AS Op, value AS Value FROM rule_conditions ORDER BY id", transaction: tx)
            .ToLookup(c => c.RuleId);
        var tags = conn.Query<TagRow>("SELECT rule_id AS RuleId, tag_id AS TagId FROM rule_tags ORDER BY tag_id", transaction: tx)
            .ToLookup(t => t.RuleId);

        return rules.Select(r => new Rule(
            r.Id, r.Name, r.Priority, r.IsActive,
            conditions[r.Id].Select(c => RuleCondition.From(c.Field, c.Op, c.Value)).ToList(),
            r.SetCategoryId,
            r.SetType is null ? null : TypeHandlers.FromDb<LineType>(r.SetType),
            r.SetPayee, r.SetRecurringId, r.SetPlanId,
            tags[r.Id].Select(t => t.TagId).ToList())).ToList();
    }
}

public sealed class MovePatternRepository(IDbConnection conn, IDbTransaction? tx = null)
{
    private sealed class Row
    {
        public long Id { get; set; }
        public long FromAccountId { get; set; }
        public string FromContains { get; set; } = "";
        public long ToAccountId { get; set; }
        public string ToContains { get; set; } = "";
        public int MaxDaysApart { get; set; }
    }

    public long Insert(MovePattern p)
    {
        conn.Execute("""
            INSERT INTO move_patterns (from_account_id, from_contains, to_account_id, to_contains, max_days_apart)
            VALUES (@FromAccountId, @FromContains, @ToAccountId, @ToContains, @MaxDaysApart)
            """, new { p.FromAccountId, p.FromContains, p.ToAccountId, p.ToContains, p.MaxDaysApart }, tx);
        return conn.ExecuteScalar<long>(Sql.LastInsertId, transaction: tx);
    }

    public IReadOnlyList<MovePattern> All() =>
        conn.Query<Row>("""
            SELECT id AS Id, from_account_id AS FromAccountId, from_contains AS FromContains, to_account_id AS ToAccountId,
                   to_contains AS ToContains, max_days_apart AS MaxDaysApart
            FROM move_patterns ORDER BY id
            """, transaction: tx)
            .Select(r => new MovePattern(r.Id, r.FromAccountId, r.FromContains, r.ToAccountId, r.ToContains, r.MaxDaysApart)).ToList();
}
