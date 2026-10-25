using System.Data;
using System.Globalization;
using Dapper;
using Microsoft.Data.Sqlite;
using Tideline.Core;
using Tideline.Data.Repositories;
using Tideline.Data.Security;

namespace Tideline.Data.Services;

public sealed record CardInput(bool IsPayInFull = true, int? StatementDay = null, int? DueDay = null, long? CreditLimitCents = null);

public sealed record LoanInput(
    string? LoanType = null,
    int? RateBps = null,
    long? PaymentCents = null,
    long? EscrowCents = null,
    int? PaymentDay = null,
    long? OriginalBalanceCents = null,
    string? StartOn = null,
    int? TermMonths = null,
    long? PaymentCategoryId = null,
    long? SecuredAssetId = null);

public sealed record RetirementInput(
    string? AccountType = null,
    long? EmployeeMonthlyCents = null,
    long? EmployerMonthlyCents = null,
    bool? IsIncludedInGoal = null,
    long? PensionAnnualAtRetirementCents = null);

public sealed record AccountInput(
    string? Name,
    string? Kind,
    string? Institution = null,
    string? BankNumber = null,
    long? OpeningBalanceCents = null,
    string? OpeningOn = null,
    bool? IsInSpread = null,
    bool? IsClosed = null,
    string? CsvUntilOn = null,
    string? Notes = null,
    int? SortOrder = null,
    bool ClearOpening = false,
    CardInput? Card = null,
    LoanInput? Loan = null,
    RetirementInput? Retirement = null,
    int? InterestRateBps = null,
    bool ClearRate = false,
    string? SimplefinAccountId = null,
    string? SimplefinFromOn = null,
    bool ClearSimplefin = false);

public sealed record PatternInput(long FromAccountId, string? FromContains, long ToAccountId, string? ToContains, int? MaxDaysApart = null);

public sealed record CategoryInput(string? Name, string? DefaultType, bool? IsArchived = null, int? SortOrder = null);

public sealed record TagInput(string? Name, string? Color = null);

public sealed record RecurringInput(
    string? Name,
    long? CategoryId,
    long? ExpectedMinCents,
    long? ExpectedMaxCents,
    string? Frequency = null,
    int? EveryN = null,
    int? DueDay = null,
    string? FirstDueOn = null,
    int? GraceDays = null,
    long? AccountId = null,
    bool? IsActive = null,
    string? EndedOn = null);

/// <summary>
/// Setting up the household: accounts, move patterns, categories, tags, recurring items and the few app settings. Every change
/// is checked, then written with its audit entry in the same database transaction (docs/09-security.md, "Audit").
/// A null field in an update means "leave it alone".
/// </summary>
public sealed class SetupEditor(IDbConnection conn, TimeProvider clock, Actor? actor = null)
{
    private static readonly string[] AccountKinds = ["checking", "savings", "credit_card", "loan", "asset", "retirement"];

    private static EditException Bad(string message) => new(EditError.Invalid, message);
    private static EditException Conflict(string message) => new(EditError.Conflict, message);
    private static EditException Missing(string what) => new(EditError.NotFound, $"There is no such {what}.");

    private T InTransaction<T>(Func<IDbTransaction, AuditWriter, T> work)
    {
        TypeHandlers.EnsureRegistered();
        using var tx = conn.BeginTransaction();
        try
        {
            var result = work(tx, new AuditWriter(conn, clock, tx, actor));
            tx.Commit();
            return result;
        }
        catch (SqliteException e) when (e.SqliteErrorCode == 19 && e.Message.Contains("UNIQUE", StringComparison.OrdinalIgnoreCase))
        {
            throw Conflict("That name or number is already used by something else.");
        }
    }

    private void InTransaction(Action<IDbTransaction, AuditWriter> work) => InTransaction<int>((tx, audit) =>
    {
        work(tx, audit);
        return 0;
    });

    private static string Name(string? name, int max, string what)
    {
        var n = name?.Trim() ?? "";
        if (n.Length == 0) throw Bad($"Give the {what} a name.");
        if (n.Length > max) throw Bad($"The {what} name is too long ({max} characters at most).");
        return n;
    }

    private static DateOnly? Day(string? text, string what)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        return DateOnly.TryParseExact(text.Trim(), "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var d) ? d : throw Bad($"{what} must be a date like 2026-03-31.");
    }

    private static LineType Type(string? text)
    {
        try { return TypeHandlers.FromDb<LineType>((text ?? "").Trim().ToLowerInvariant()); }
        catch (InvalidDataException) { throw Bad($"'{text}' is not a type. Use income, bill, required, extra, one_time or savings."); }
    }

    // ---- accounts ----------------------------------------------------------------------------------------------

    private sealed class AccountRow
    {
        public long Id { get; set; }
        public string Name { get; set; } = "";
        public string Kind { get; set; } = "";
        public string? Institution { get; set; }
        public string? BankNumberHash { get; set; }
        public string? BankNumberEnc { get; set; }
        public long? OpeningBalanceCents { get; set; }
        public DateOnly? OpeningOn { get; set; }
        public bool IsInSpread { get; set; }
        public bool IsClosed { get; set; }
        public DateOnly? CsvUntilOn { get; set; }
        public string? Notes { get; set; }
        public int SortOrder { get; set; }
        public int? InterestRateBps { get; set; }
        public string? SimplefinAccountId { get; set; }
        public DateOnly? SimplefinFromOn { get; set; }
    }

    private AccountRow? Account(long id, IDbTransaction tx) => conn.QuerySingleOrDefault<AccountRow>("""
        SELECT id AS Id, name AS Name, kind AS Kind, institution AS Institution, bank_number_hash AS BankNumberHash, bank_number_enc AS BankNumberEnc, opening_balance_cents AS OpeningBalanceCents,
               opening_on AS OpeningOn, is_in_spread AS IsInSpread, is_closed AS IsClosed, csv_until_on AS CsvUntilOn, notes AS Notes, sort_order AS SortOrder, interest_rate_bps AS InterestRateBps,
               simplefin_account_id AS SimplefinAccountId, simplefin_from_on AS SimplefinFromOn
        FROM accounts WHERE id = @id
        """, new { id }, tx);

    private object AccountSnapshot(long id, IDbTransaction tx)
    {
        var a = Account(id, tx)!;
        var terms = conn.QuerySingleOrDefault<(bool Pay, int? Statement, int? Due, long? Limit)?>("SELECT is_pay_in_full, statement_day, due_day, credit_limit_cents FROM card_terms WHERE account_id = @id", new { id }, tx);
        var card = terms is { } t ? new { isPayInFull = t.Pay, statementDay = t.Statement, dueDay = t.Due, creditLimitCents = t.Limit } : null;
        var loan = conn.QuerySingleOrDefault<(string Type, int? Rate, long? Payment, long? Escrow, int? Day, long? Original, string? Start, int? Term, long? Category, long? Secured)?>(
            "SELECT loan_type, rate_bps, payment_cents, escrow_cents, payment_day, original_balance_cents, start_on, term_months, payment_category_id, secured_asset_id FROM loan_terms WHERE account_id = @id", new { id }, tx);
        var retirement = conn.QuerySingleOrDefault<(string Type, long Employee, long Employer, bool Included, long? Pension)?>(
            "SELECT account_type, employee_contribution_monthly_cents, employer_contribution_monthly_cents, is_included_in_goal, pension_annual_at_retirement_cents FROM retirement_terms WHERE account_id = @id", new { id }, tx);
        // accountNumberKey says whether the number changed, without carrying any digits
        return new
        {
            a.Name, a.Kind, a.Institution, accountNumberKey = a.BankNumberHash is { Length: >= 8 } h ? h[..8] : null, a.OpeningBalanceCents, a.OpeningOn, a.IsInSpread, a.IsClosed, a.CsvUntilOn, a.Notes, a.SortOrder, a.InterestRateBps, a.SimplefinAccountId, a.SimplefinFromOn, card,
            loan = loan is { } l ? new { loanType = l.Type, rateBps = l.Rate, paymentCents = l.Payment, escrowCents = l.Escrow, paymentDay = l.Day, originalBalanceCents = l.Original, startOn = l.Start, termMonths = l.Term, paymentCategoryId = l.Category, securedAssetId = l.Secured } : null,
            retirement = retirement is { } r ? new { accountType = r.Type, employeeMonthlyCents = r.Employee, employerMonthlyCents = r.Employer, isIncludedInGoal = r.Included, pensionAnnualAtRetirementCents = r.Pension } : null,
        };
    }

    private static void CheckCard(CardInput c)
    {
        if (c.StatementDay is < 1 or > 31) throw Bad("The statement day must be between 1 and 31.");
        if (c.DueDay is < 1 or > 31) throw Bad("The due day must be between 1 and 31.");
        if (c.CreditLimitCents is < 0) throw Bad("The credit limit cannot be negative.");
    }

    private void SaveCard(long accountId, CardInput c, IDbTransaction tx)
    {
        CheckCard(c);
        conn.Execute("DELETE FROM card_terms WHERE account_id = @accountId", new { accountId }, tx);
        conn.Execute("INSERT INTO card_terms (account_id, is_pay_in_full, statement_day, due_day, credit_limit_cents) VALUES (@accountId, @pay, @statement, @due, @limit)",
            new { accountId, pay = c.IsPayInFull ? 1 : 0, statement = c.StatementDay, due = c.DueDay, limit = c.CreditLimitCents }, tx);
    }

    /// <summary>Only the last 4 digits are kept, and never as digits: a keyed hash to match files and rules, and a sealed copy to show as ••••1111. Anything before them is thrown away.</summary>
    private static (string? Hash, string? Sealed) SecureNumber(string? typed)
    {
        if (string.IsNullOrWhiteSpace(typed)) return (null, null);
        var last = AccountNumbers.LastFour(typed) ?? throw Bad("Enter at least the last 4 digits of the account number. Only the last 4 are kept.");
        return (AccountNumbers.Keys.Hash(last), AccountNumbers.Keys.Seal(last));
    }

    private static int? RateOf(int? bps) => bps is < 0 or > 10000 ? throw Bad("The interest rate must be between 0% and 100%.") : bps;

    private bool Exists(string table, long id, IDbTransaction tx) => conn.ExecuteScalar<long>($"SELECT COUNT(*) FROM {table} WHERE id = @id", new { id }, tx) > 0;

    private static readonly string[] LoanTypes = ["mortgage", "auto", "student", "personal", "family", "other"];
    private static readonly string[] RetirementTypes = ["401k", "403b", "457", "ira", "roth_ira", "pension", "hsa", "brokerage", "other"];

    private void SaveLoan(long accountId, LoanInput l, IDbTransaction tx)
    {
        var type = (l.LoanType ?? "other").Trim().ToLowerInvariant();
        if (!LoanTypes.Contains(type)) throw Bad("The loan type must be mortgage, auto, student, personal, family or other.");
        if (l.RateBps is < 0 or > 10000 * 3) throw Bad("The interest rate must be between 0% and 300%.");
        if (l.PaymentCents is < 0 || l.EscrowCents is < 0 || l.OriginalBalanceCents is < 0) throw Bad("Amounts cannot be negative.");
        if (l.PaymentDay is < 1 or > 31) throw Bad("The payment day must be between 1 and 31.");
        if (l.TermMonths is < 1 or > 1200) throw Bad("The term is a number of months, 1 to 1200.");
        var start = Day(l.StartOn, "The start date");
        if (l.PaymentCategoryId is { } c && !Exists("categories", c, tx)) throw Bad("That payment category does not exist.");
        if (l.SecuredAssetId is { } s && (s == accountId || !Exists("accounts", s, tx))) throw Bad("The secured asset must be a different, existing account.");
        conn.Execute("DELETE FROM loan_terms WHERE account_id = @accountId", new { accountId }, tx);
        conn.Execute("""
            INSERT INTO loan_terms (account_id, loan_type, rate_bps, payment_cents, escrow_cents, payment_day, original_balance_cents, start_on, term_months, payment_category_id, secured_asset_id)
            VALUES (@accountId, @type, @rate, @payment, @escrow, @day, @original, @start, @term, @category, @secured)
            """, new { accountId, type, rate = l.RateBps, payment = l.PaymentCents, escrow = l.EscrowCents, day = l.PaymentDay, original = l.OriginalBalanceCents, start, term = l.TermMonths, category = l.PaymentCategoryId, secured = l.SecuredAssetId }, tx);
    }

    private void SaveRetirement(long accountId, RetirementInput r, IDbTransaction tx)
    {
        var type = (r.AccountType ?? "other").Trim().ToLowerInvariant();
        if (!RetirementTypes.Contains(type)) throw Bad("The account type must be 401k, 403b, 457, ira, roth_ira, pension, hsa, brokerage or other.");
        if (r.EmployeeMonthlyCents is < 0 || r.EmployerMonthlyCents is < 0 || r.PensionAnnualAtRetirementCents is < 0) throw Bad("Amounts cannot be negative.");
        conn.Execute("DELETE FROM retirement_terms WHERE account_id = @accountId", new { accountId }, tx);
        conn.Execute("""
            INSERT INTO retirement_terms (account_id, account_type, employee_contribution_monthly_cents, employer_contribution_monthly_cents, is_included_in_goal, pension_annual_at_retirement_cents)
            VALUES (@accountId, @type, @employee, @employer, @included, @pension)
            """, new { accountId, type, employee = r.EmployeeMonthlyCents ?? 0, employer = r.EmployerMonthlyCents ?? 0, included = (r.IsIncludedInGoal ?? true) ? 1 : 0, pension = r.PensionAnnualAtRetirementCents }, tx);
    }

    public long CreateAccount(AccountInput a) => InTransaction((tx, audit) =>
    {
        var name = Name(a.Name, 100, "account");
        var kind = (a.Kind ?? "").Trim().ToLowerInvariant();
        if (!AccountKinds.Contains(kind)) throw Bad("The kind must be checking, savings, credit_card, loan, asset or retirement.");
        var (bankHash, bankEnc) = SecureNumber(a.BankNumber);
        var openingOn = Day(a.OpeningOn, "The opening date");
        if (a.OpeningBalanceCents is not null && openingOn is null) throw Bad("Say which day the opening balance is for.");
        var csvUntil = Day(a.CsvUntilOn, "The CSV cutover date");

        var inSpread = a.IsInSpread ?? kind is "checking" or "savings" or "credit_card";
        conn.Execute("""
            INSERT INTO accounts (name, kind, institution, bank_number_hash, bank_number_enc, opening_balance_cents, opening_on, is_in_spread, is_closed, sort_order, csv_until_on, notes, interest_rate_bps, created_at)
            VALUES (@name, @kind, @institution, @bankHash, @bankEnc, @opening, @openingOn, @inSpread, @closed, @sort, @csvUntil, @notes, @rate, @now)
            """, new
        {
            name, kind, institution = Blank(a.Institution), bankHash, bankEnc, opening = a.OpeningBalanceCents, openingOn,
            inSpread = inSpread ? 1 : 0, closed = a.IsClosed == true ? 1 : 0, sort = a.SortOrder ?? 0, csvUntil, notes = Blank(a.Notes), rate = RateOf(a.InterestRateBps), now = clock.GetUtcNow(),
        }, tx);
        var id = conn.ExecuteScalar<long>(Sql.LastInsertId, transaction: tx);
        if (kind == "credit_card") SaveCard(id, a.Card ?? new CardInput(), tx);
        if (kind == "loan") { SaveLoan(id, a.Loan ?? new LoanInput(), tx); LoanTracker.Refresh(conn, clock, tx); }
        if (kind == "retirement") SaveRetirement(id, a.Retirement ?? new RetirementInput(), tx);
        audit.Write("account.created", "account", id, null, AccountSnapshot(id, tx));
        return id;
    });

    public void UpdateAccount(long id, AccountInput a) => InTransaction((tx, audit) =>
    {
        var current = Account(id, tx) ?? throw Missing("account");
        var before = AccountSnapshot(id, tx);

        var name = a.Name is null ? current.Name : Name(a.Name, 100, "account");
        // not sent: leave it; sent empty: clear it; sent digits: keep only the last 4, as a hash and a sealed copy
        var (bankHash, bankEnc) = a.BankNumber is null ? (current.BankNumberHash, current.BankNumberEnc) : SecureNumber(a.BankNumber);
        DateOnly? openingOn = a.OpeningOn is null ? current.OpeningOn : Day(a.OpeningOn, "The opening date");
        var opening = a.ClearOpening ? null : a.OpeningBalanceCents ?? current.OpeningBalanceCents;
        if (a.ClearOpening) openingOn = null;
        if (opening is not null && openingOn is null) throw Bad("Say which day the opening balance is for.");
        var csvUntil = a.CsvUntilOn is null ? current.CsvUntilOn : Day(a.CsvUntilOn, "The CSV cutover date");

        var sfin = a.ClearSimplefin ? null : Blank(a.SimplefinAccountId) ?? current.SimplefinAccountId;
        var sfinFrom = a.ClearSimplefin ? null : a.SimplefinFromOn is null ? current.SimplefinFromOn : Day(a.SimplefinFromOn, "The SimpleFIN start date");
        if (sfin is not null && conn.ExecuteScalar<long>("SELECT COUNT(*) FROM accounts WHERE simplefin_account_id = @sfin AND id <> @id", new { sfin, id }, tx) > 0)
            throw Bad("That SimpleFIN account is already matched to another account.");
        if (sfin is not null && sfinFrom is null) throw Bad("Say the day SimpleFIN takes over from the CSV files, so the two do not overlap.");

        conn.Execute("""
            UPDATE accounts SET name = @name, institution = @institution, bank_number_hash = @bankHash, bank_number_enc = @bankEnc, opening_balance_cents = @opening, opening_on = @openingOn,
                is_in_spread = @inSpread, is_closed = @closed, sort_order = @sort, csv_until_on = @csvUntil, notes = @notes, interest_rate_bps = @rate,
                simplefin_account_id = @sfin, simplefin_from_on = @sfinFrom
            WHERE id = @id
            """, new
        {
            id, name, institution = a.Institution is null ? current.Institution : Blank(a.Institution), bankHash, bankEnc,
            opening, openingOn, inSpread = (a.IsInSpread ?? current.IsInSpread) ? 1 : 0, closed = (a.IsClosed ?? current.IsClosed) ? 1 : 0,
            sort = a.SortOrder ?? current.SortOrder, csvUntil, notes = a.Notes is null ? current.Notes : Blank(a.Notes),
            rate = a.ClearRate ? null : RateOf(a.InterestRateBps) ?? current.InterestRateBps,
            sfin = sfin, sfinFrom = sfinFrom,
        }, tx);
        if (a.Card is not null)
        {
            if (current.Kind != "credit_card") throw Bad("Only a credit card has card terms.");
            SaveCard(id, a.Card, tx);
        }
        if (a.Loan is not null)
        {
            if (current.Kind != "loan") throw Bad("Only a loan has loan terms.");
            SaveLoan(id, a.Loan, tx);
            LoanTracker.Refresh(conn, clock, tx);
        }
        if (a.Retirement is not null)
        {
            if (current.Kind != "retirement") throw Bad("Only a retirement account has retirement terms.");
            SaveRetirement(id, a.Retirement, tx);
        }
        audit.Write("account.updated", "account", id, before, AccountSnapshot(id, tx));
    });

    // ---- move patterns -----------------------------------------------------------------------------------------

    private void CheckPattern(PatternInput p, IDbTransaction tx)
    {
        if (string.IsNullOrWhiteSpace(p.FromContains) || string.IsNullOrWhiteSpace(p.ToContains)) throw Bad("Both sides need the text to look for in the description.");
        if (p.FromAccountId == p.ToAccountId) throw Bad("A transfer goes between two different accounts.");
        if (p.MaxDaysApart is < 0 or > 30) throw Bad("Days apart must be between 0 and 30.");
        foreach (var id in new[] { p.FromAccountId, p.ToAccountId })
            if (Account(id, tx) is null) throw Bad($"There is no account {id}.");
    }

    public long CreatePattern(PatternInput p) => InTransaction((tx, audit) =>
    {
        CheckPattern(p, tx);
        conn.Execute("INSERT INTO move_patterns (from_account_id, from_contains, to_account_id, to_contains, max_days_apart) VALUES (@f, @fc, @t, @tc, @days)",
            new { f = p.FromAccountId, fc = p.FromContains!.Trim(), t = p.ToAccountId, tc = p.ToContains!.Trim(), days = p.MaxDaysApart ?? 5 }, tx);
        var id = conn.ExecuteScalar<long>(Sql.LastInsertId, transaction: tx);
        audit.Write("move_pattern.created", "move_pattern", id, null, p);
        return id;
    });

    public void UpdatePattern(long id, PatternInput p) => InTransaction((tx, audit) =>
    {
        var before = conn.QuerySingleOrDefault<(long F, string Fc, long T, string Tc, int Days)?>("SELECT from_account_id, from_contains, to_account_id, to_contains, max_days_apart FROM move_patterns WHERE id = @id", new { id }, tx)
            ?? throw Missing("transfer pattern");
        CheckPattern(p, tx);
        conn.Execute("UPDATE move_patterns SET from_account_id = @f, from_contains = @fc, to_account_id = @t, to_contains = @tc, max_days_apart = @days WHERE id = @id",
            new { id, f = p.FromAccountId, fc = p.FromContains!.Trim(), t = p.ToAccountId, tc = p.ToContains!.Trim(), days = p.MaxDaysApart ?? before.Days }, tx);
        audit.Write("move_pattern.updated", "move_pattern", id, before, p);
    });

    public void DeletePattern(long id) => InTransaction((tx, audit) =>
    {
        var before = conn.QuerySingleOrDefault<(long F, string Fc, long T, string Tc, int Days)?>("SELECT from_account_id, from_contains, to_account_id, to_contains, max_days_apart FROM move_patterns WHERE id = @id", new { id }, tx)
            ?? throw Missing("transfer pattern");
        conn.Execute("DELETE FROM move_patterns WHERE id = @id", new { id }, tx);
        audit.Write("move_pattern.deleted", "move_pattern", id, before, null);
        return 0;
    });

    // ---- categories ----------------------------------------------------------------------------------------------

    private object? CategorySnapshot(long id, IDbTransaction tx) =>
        conn.QuerySingleOrDefault<(string Name, string DefaultType, bool IsArchived, int SortOrder, long? MergedIntoId)?>(
            "SELECT name, default_type, is_archived, sort_order, merged_into_id FROM categories WHERE id = @id", new { id }, tx) is { } c
            ? new { c.Name, c.DefaultType, c.IsArchived, c.SortOrder, c.MergedIntoId } : null;

    public long CreateCategory(CategoryInput c) => InTransaction((tx, audit) =>
    {
        var name = Name(c.Name, 100, "category");
        var type = Type(c.DefaultType);
        conn.Execute("INSERT INTO categories (name, default_type, sort_order) VALUES (@name, @type, @sort)", new { name, type = type.ToDb(), sort = c.SortOrder ?? 0 }, tx);
        var id = conn.ExecuteScalar<long>(Sql.LastInsertId, transaction: tx);
        audit.Write("category.created", "category", id, null, CategorySnapshot(id, tx));
        return id;
    });

    public void UpdateCategory(long id, CategoryInput c) => InTransaction((tx, audit) =>
    {
        var before = CategorySnapshot(id, tx) ?? throw Missing("category");
        var current = conn.QuerySingle<(string Name, string Type, bool Archived, int Sort)>("SELECT name, default_type, is_archived, sort_order FROM categories WHERE id = @id", new { id }, tx);
        conn.Execute("UPDATE categories SET name = @name, default_type = @type, is_archived = @archived, sort_order = @sort WHERE id = @id", new
        {
            id,
            name = c.Name is null ? current.Name : Name(c.Name, 100, "category"),
            type = c.DefaultType is null ? current.Type : Type(c.DefaultType).ToDb(),
            archived = (c.IsArchived ?? current.Archived) ? 1 : 0,
            sort = c.SortOrder ?? current.Sort,
        }, tx);
        audit.Write("category.updated", "category", id, before, CategorySnapshot(id, tx));
    });

    /// <summary>Everything that pointed at <paramref name="id"/> now points at <paramref name="intoId"/>, then <paramref name="id"/> is archived.</summary>
    public int MergeCategory(long id, long intoId) => InTransaction((tx, audit) =>
    {
        if (id == intoId) throw Bad("Pick a different category to merge into.");
        var before = CategorySnapshot(id, tx) ?? throw Missing("category");
        if (CategorySnapshot(intoId, tx) is null) throw Bad("The category to merge into does not exist.");

        var moved = 0;
        foreach (var sql in new[]
        {
            "UPDATE transactions SET category_id = @into WHERE category_id = @id",
            "UPDATE split_lines SET category_id = @into WHERE category_id = @id",
            "UPDATE rules SET set_category_id = @into WHERE set_category_id = @id",
            "UPDATE recurring SET category_id = @into WHERE category_id = @id",
            "UPDATE loan_terms SET payment_category_id = @into WHERE payment_category_id = @id",
        })
            moved += conn.Execute(sql, new { id, into = intoId }, tx);

        // a budget line for both would clash: keep the target's
        conn.Execute("DELETE FROM budget_lines WHERE category_id = @id AND budget_id IN (SELECT budget_id FROM budget_lines WHERE category_id = @into)", new { id, into = intoId }, tx);
        moved += conn.Execute("UPDATE budget_lines SET category_id = @into WHERE category_id = @id", new { id, into = intoId }, tx);
        conn.Execute("UPDATE categories SET is_archived = 1, merged_into_id = @into WHERE id = @id", new { id, into = intoId }, tx);

        audit.Write("category.merged", "category", id, before, new { mergedInto = intoId, rowsMoved = moved });
        return moved;
    });

    // ---- tags ----------------------------------------------------------------------------------------------------

    private static string TagName(string? raw)
    {
        var name = string.Join('-', (raw ?? "").Trim().ToLowerInvariant().Split([' ', '\t'], StringSplitOptions.RemoveEmptyEntries));
        if (name.Length == 0) throw Bad("A tag needs a name.");
        if (name.Length > 50) throw Bad("A tag name is 50 characters at most.");
        return name;
    }

    public long CreateTag(TagInput t) => InTransaction((tx, audit) =>
    {
        var name = TagName(t.Name);
        conn.Execute("INSERT INTO tags (name, color) VALUES (@name, @color)", new { name, color = Blank(t.Color) }, tx);
        var id = conn.ExecuteScalar<long>(Sql.LastInsertId, transaction: tx);
        audit.Write("tag.created", "tag", id, null, new { name, color = Blank(t.Color) });
        return id;
    });

    public void UpdateTag(long id, TagInput t) => InTransaction((tx, audit) =>
    {
        var before = conn.QuerySingleOrDefault<(string Name, string? Color)?>("SELECT name, color FROM tags WHERE id = @id", new { id }, tx) ?? throw Missing("tag");
        var name = t.Name is null ? before.Name : TagName(t.Name);
        var color = t.Color is null ? before.Color : Blank(t.Color);
        conn.Execute("UPDATE tags SET name = @name, color = @color WHERE id = @id", new { id, name, color }, tx);
        audit.Write("tag.updated", "tag", id, new { before.Name, before.Color }, new { name, color });
        return 0;
    });

    /// <summary>Removes the tag from every line, split and rule, then the tag itself.</summary>
    public int DeleteTag(long id) => InTransaction((tx, audit) =>
    {
        var before = conn.QuerySingleOrDefault<(string Name, string? Color)?>("SELECT name, color FROM tags WHERE id = @id", new { id }, tx) ?? throw Missing("tag");
        var removed = conn.Execute("DELETE FROM transaction_tags WHERE tag_id = @id", new { id }, tx)
                      + conn.Execute("DELETE FROM split_line_tags WHERE tag_id = @id", new { id }, tx)
                      + conn.Execute("DELETE FROM rule_tags WHERE tag_id = @id", new { id }, tx);
        conn.Execute("DELETE FROM tags WHERE id = @id", new { id }, tx);
        audit.Write("tag.deleted", "tag", id, new { before.Name, before.Color }, new { removedFrom = removed });
        return removed;
    });

    // ---- recurring -----------------------------------------------------------------------------------------------

    private object? RecurringSnapshot(long id, IDbTransaction tx) =>
        conn.QuerySingleOrDefault<(string Name, long CategoryId, long Min, long Max, string Frequency, int EveryN, int DueDay, string FirstDueOn, int Grace, long? AccountId, bool IsActive, string? EndedOn)?>(
            "SELECT name, category_id, expected_min_cents, expected_max_cents, frequency, every_n, due_day, first_due_on, grace_days, account_id, is_active, ended_on FROM recurring WHERE id = @id", new { id }, tx);

    private sealed class RecurringRow
    {
        public string Name { get; set; } = "";
        public long CategoryId { get; set; }
        public long Min { get; set; }
        public long Max { get; set; }
        public string Frequency { get; set; } = "monthly";
        public int EveryN { get; set; }
        public int DueDay { get; set; }
        public DateOnly FirstDueOn { get; set; }
        public int Grace { get; set; }
        public long? AccountId { get; set; }
        public bool IsActive { get; set; }
        public DateOnly? EndedOn { get; set; }
    }

    private RecurringRow Merge(RecurringInput r, IDbTransaction tx, long? existingId)
    {
        var cur = existingId is { } e
            ? conn.QuerySingleOrDefault<RecurringRow>("""
                SELECT name AS Name, category_id AS CategoryId, expected_min_cents AS Min, expected_max_cents AS Max, frequency AS Frequency, every_n AS EveryN,
                       due_day AS DueDay, first_due_on AS FirstDueOn, grace_days AS Grace, account_id AS AccountId, is_active AS IsActive, ended_on AS EndedOn
                FROM recurring WHERE id = @e
                """, new { e }, tx) ?? throw Missing("recurring item")
            : null;

        var name = r.Name is null && cur is not null ? cur.Name : Name(r.Name, 100, "recurring item");
        var categoryId = r.CategoryId ?? cur?.CategoryId ?? throw Bad("Pick a category.");
        if (conn.ExecuteScalar<long>("SELECT COUNT(*) FROM categories WHERE id = @categoryId", new { categoryId }, tx) == 0) throw Bad("That category does not exist.");
        var min = r.ExpectedMinCents ?? cur?.Min ?? throw Bad("Give the lowest amount you expect.");
        var max = r.ExpectedMaxCents ?? cur?.Max ?? throw Bad("Give the highest amount you expect.");
        if (min < 0 || max < min) throw Bad("The highest amount cannot be below the lowest.");
        var frequency = (r.Frequency ?? cur?.Frequency ?? "monthly").Trim().ToLowerInvariant();
        if (frequency is not ("monthly" or "yearly")) throw Bad("How often must be monthly or yearly.");
        var everyN = r.EveryN ?? cur?.EveryN ?? 1;
        if (everyN is < 1 or > 24) throw Bad("Every N months must be between 1 and 24.");
        var dueDay = r.DueDay ?? cur?.DueDay ?? throw Bad("Give the day of the month it is due.");
        if (dueDay is < 1 or > 31) throw Bad("The due day must be between 1 and 31.");
        var first = Day(r.FirstDueOn, "The first due date") ?? cur?.FirstDueOn ?? throw Bad("Give the first date it was due.");
        var grace = r.GraceDays ?? cur?.Grace ?? 5;
        if (grace is < 0 or > 60) throw Bad("Grace days must be between 0 and 60.");
        DateOnly? ended = r.EndedOn is null ? cur?.EndedOn : Day(r.EndedOn, "The end date");

        return new RecurringRow
        {
            Name = name, CategoryId = categoryId, Min = min, Max = max, Frequency = frequency, EveryN = everyN, DueDay = dueDay,
            FirstDueOn = first, Grace = grace, AccountId = r.AccountId ?? cur?.AccountId, IsActive = r.IsActive ?? cur?.IsActive ?? true, EndedOn = ended,
        };
    }

    public long CreateRecurring(RecurringInput r) => InTransaction((tx, audit) =>
    {
        var v = Merge(r, tx, null);
        conn.Execute("""
            INSERT INTO recurring (name, category_id, expected_min_cents, expected_max_cents, frequency, every_n, due_day, first_due_on, grace_days, account_id, is_active, ended_on)
            VALUES (@Name, @CategoryId, @Min, @Max, @Frequency, @EveryN, @DueDay, @FirstDueOn, @Grace, @AccountId, @active, @EndedOn)
            """, new { v.Name, v.CategoryId, v.Min, v.Max, v.Frequency, v.EveryN, v.DueDay, v.FirstDueOn, v.Grace, v.AccountId, active = v.IsActive ? 1 : 0, v.EndedOn }, tx);
        var id = conn.ExecuteScalar<long>(Sql.LastInsertId, transaction: tx);
        audit.Write("recurring.created", "recurring", id, null, RecurringSnapshot(id, tx));
        return id;
    });

    public void UpdateRecurring(long id, RecurringInput r) => InTransaction((tx, audit) =>
    {
        var before = RecurringSnapshot(id, tx) ?? throw Missing("recurring item");
        var v = Merge(r, tx, id);
        conn.Execute("""
            UPDATE recurring SET name = @Name, category_id = @CategoryId, expected_min_cents = @Min, expected_max_cents = @Max, frequency = @Frequency, every_n = @EveryN,
                due_day = @DueDay, first_due_on = @FirstDueOn, grace_days = @Grace, account_id = @AccountId, is_active = @active, ended_on = @EndedOn
            WHERE id = @id
            """, new { id, v.Name, v.CategoryId, v.Min, v.Max, v.Frequency, v.EveryN, v.DueDay, v.FirstDueOn, v.Grace, v.AccountId, active = v.IsActive ? 1 : 0, v.EndedOn }, tx);
        audit.Write("recurring.updated", "recurring", id, before, RecurringSnapshot(id, tx));
        return 0;
    });

    // ---- app settings --------------------------------------------------------------------------------------------

    public const string OneTimeAttentionKey = "one_time_attention_cents";
    public const long DefaultOneTimeAttentionCents = 50000;

    public static long OneTimeAttentionCents(IDbConnection conn, IDbTransaction? tx = null)
    {
        var raw = new SettingsRepository(conn, TimeProvider.System, tx).Get(OneTimeAttentionKey);
        return long.TryParse(raw, out var v) && v >= 0 ? v : DefaultOneTimeAttentionCents;
    }

    public void SetOneTimeAttention(long cents) => InTransaction((tx, audit) =>
    {
        if (cents < 0 || cents > 100_000_000) throw Bad("That amount is out of range.");
        var before = OneTimeAttentionCents(conn, tx);
        new SettingsRepository(conn, clock, tx).Set(OneTimeAttentionKey, cents.ToString(CultureInfo.InvariantCulture));
        audit.Write("settings.updated", "setting", OneTimeAttentionKey, new { cents = before }, new { cents });
        return 0;
    });

    public const string CardInterestCategoryKey = "card_interest_category_id";
    public const string DebtWordsKey = "debt_words";

    /// <summary>The category whose lines on a card mean interest or fees (null: not chosen), and the words that make a payment look like a new loan.</summary>
    public static (long? CardInterestCategoryId, string DebtWords) NotifySettings(IDbConnection conn)
    {
        var settings = new SettingsRepository(conn, TimeProvider.System);
        return (long.TryParse(settings.Get(CardInterestCategoryKey), out var id) ? id : null, settings.Get(DebtWordsKey) ?? string.Join(", ", DefaultDebtWords));
    }

    public static readonly string[] DefaultDebtWords = ["LOAN", "FINANCING", "FINANCE CO", "AFFIRM", "KLARNA", "AFTERPAY", "SYNCHRONY", "CREDIT ONE", "CAPITAL ONE", "LENDING"];

    public void SetNotifySettings(long? cardInterestCategoryId, bool clearCardInterest, string? debtWords) => InTransaction((tx, audit) =>
    {
        var before = NotifySettings(conn);
        var settings = new SettingsRepository(conn, clock, tx);
        if (clearCardInterest) settings.Delete(CardInterestCategoryKey);
        else if (cardInterestCategoryId is { } c)
        {
            if (!Exists("categories", c, tx)) throw Bad("That category does not exist.");
            settings.Set(CardInterestCategoryKey, c.ToString(CultureInfo.InvariantCulture));
        }
        if (debtWords is not null)
        {
            var words = debtWords.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Select(w => w.ToUpperInvariant()).Distinct().ToList();
            if (words.Count == 0) settings.Delete(DebtWordsKey);
            else if (words.Any(w => w.Length > 40) || words.Count > 50) throw Bad("Give up to 50 words or phrases, each 40 characters at most.");
            else settings.Set(DebtWordsKey, string.Join(", ", words));
        }
        var after = NotifySettings(conn);
        audit.Write("settings.updated", "setting", "notify", new { before.CardInterestCategoryId, before.DebtWords }, new { after.CardInterestCategoryId, after.DebtWords });
        return 0;
    });

    private static string? Blank(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();
}
