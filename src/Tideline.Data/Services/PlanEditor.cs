using System.Data;
using Dapper;
using Tideline.Data.Repositories;

namespace Tideline.Data.Services;

public sealed record PlanInput(
    string? Name,
    long? TargetCents = null,
    string? StartOn = null,
    string? TargetOn = null,
    long? StartAmountCents = null,
    long? FundingAccountId = null,
    string? ProgressMode = null,
    bool? IsTrackable = null,
    string? Status = null,
    string? Notes = null,
    bool ClearFunding = false);

public sealed record BudgetLineInput(long CategoryId, long? MonthlyCents = null, long? AnnualCents = null);

public sealed record BudgetInput(long? ExpectedIncomeCents = null, int? PopPercent = null, long? PopMinCents = null, IReadOnlyList<BudgetLineInput>? Lines = null, bool ClearExpectedIncome = false);

/// <summary>Plans and the yearly budget. Each change is audited with before and after in the same transaction.</summary>
public sealed class PlanEditor(IDbConnection conn, TimeProvider clock, Actor? actor = null) : EditorBase(conn, clock, actor)
{
    private static readonly string[] Modes = ["account_balance", "linked_lines"];
    private static readonly string[] Statuses = ["active", "done", "cancelled"];

    // ---- plans ----------------------------------------------------------------------------------------------------

    private object? Plan(long id, IDbTransaction tx) => Conn.QuerySingleOrDefault<(string Name, long Target, string Start, string TargetOn, long StartAmount, long? Account, string Mode, bool Trackable, string Status, string? Notes)?>(
        "SELECT name, target_cents, start_on, target_on, start_amount_cents, funding_account_id, progress_mode, is_trackable, status, notes FROM plans WHERE id = @id", new { id }, tx) is { } p
        ? new { name = p.Name, targetCents = p.Target, startOn = p.Start, targetOn = p.TargetOn, startAmountCents = p.StartAmount, fundingAccountId = p.Account, progressMode = p.Mode, isTrackable = p.Trackable, status = p.Status, notes = p.Notes }
        : null;

    public long CreatePlan(PlanInput p) => InTransaction((tx, audit) =>
    {
        var name = Name(p.Name, 100, "plan");
        var start = Day(p.StartOn, "The start date") ?? DateOnly.FromDateTime(Clock.GetUtcNow().UtcDateTime);
        var target = Day(p.TargetOn, "The target date") ?? throw Bad("Say the date the plan should be finished by.");
        if (p.TargetCents is not > 0) throw Bad("The target amount has to be more than zero.");
        if (target <= start) throw Bad("The target date has to be after the start date.");
        var mode = (p.ProgressMode ?? "linked_lines").Trim().ToLowerInvariant();
        if (!Modes.Contains(mode)) throw Bad("Progress is counted from an account balance or from lines you link to the plan.");
        if (mode == "account_balance" && p.FundingAccountId is null) throw Bad("A plan that follows an account balance needs the account.");
        if (p.FundingAccountId is { } a && !Exists("accounts", a, tx)) throw Bad("That account does not exist.");
        if (p.StartAmountCents is < 0) throw Bad("The amount to start from cannot be negative.");

        Conn.Execute("""
            INSERT INTO plans (name, target_cents, start_on, target_on, start_amount_cents, funding_account_id, progress_mode, is_trackable, status, notes, created_at)
            VALUES (@name, @target, @start, @targetOn, @startAmount, @account, @mode, @trackable, 'active', @notes, @now)
            """, new
        {
            name, target = p.TargetCents, start, targetOn = target, startAmount = p.StartAmountCents ?? 0, account = p.FundingAccountId, mode,
            trackable = (p.IsTrackable ?? true) ? 1 : 0, notes = Blank(p.Notes), now = Clock.GetUtcNow(),
        }, tx);
        var id = Conn.ExecuteScalar<long>(Sql.LastInsertId, transaction: tx);
        audit.Write("plan.created", "plan", id, null, Plan(id, tx));
        return id;
    });

    public void UpdatePlan(long id, PlanInput p) => InTransaction((tx, audit) =>
    {
        var cur = Conn.QuerySingleOrDefault<(string Name, long Target, DateOnly Start, DateOnly TargetOn, long StartAmount, long? Account, string Mode, bool Trackable, string Status, string? Notes)?>(
            "SELECT name, target_cents, start_on, target_on, start_amount_cents, funding_account_id, progress_mode, is_trackable, status, notes FROM plans WHERE id = @id", new { id }, tx)
            ?? throw Missing("plan");
        var before = Plan(id, tx);

        var name = p.Name is null ? cur.Name : Name(p.Name, 100, "plan");
        var target = p.TargetCents ?? cur.Target;
        var start = p.StartOn is null ? cur.Start : Day(p.StartOn, "The start date")!.Value;
        var targetOn = p.TargetOn is null ? cur.TargetOn : Day(p.TargetOn, "The target date")!.Value;
        if (target <= 0) throw Bad("The target amount has to be more than zero.");
        if (targetOn <= start) throw Bad("The target date has to be after the start date.");
        var mode = (p.ProgressMode ?? cur.Mode).Trim().ToLowerInvariant();
        if (!Modes.Contains(mode)) throw Bad("Progress is counted from an account balance or from lines you link to the plan.");
        var account = p.ClearFunding ? null : p.FundingAccountId ?? cur.Account;
        if (mode == "account_balance" && account is null) throw Bad("A plan that follows an account balance needs the account.");
        if (account is { } a && !Exists("accounts", a, tx)) throw Bad("That account does not exist.");
        var status = (p.Status ?? cur.Status).Trim().ToLowerInvariant();
        if (!Statuses.Contains(status)) throw Bad("The status is active, done or cancelled.");
        if ((p.StartAmountCents ?? cur.StartAmount) < 0) throw Bad("The amount to start from cannot be negative.");

        Conn.Execute("""
            UPDATE plans SET name = @name, target_cents = @target, start_on = @start, target_on = @targetOn, start_amount_cents = @startAmount,
                funding_account_id = @account, progress_mode = @mode, is_trackable = @trackable, status = @status, notes = @notes
            WHERE id = @id
            """, new
        {
            id, name, target, start, targetOn, startAmount = p.StartAmountCents ?? cur.StartAmount, account, mode, trackable = (p.IsTrackable ?? cur.Trackable) ? 1 : 0, status,
            notes = p.Notes is null ? cur.Notes : Blank(p.Notes),
        }, tx);
        audit.Write("plan.updated", "plan", id, before, Plan(id, tx));
    });

    // ---- the yearly budget -----------------------------------------------------------------------------------------

    private object? Budget(long id, IDbTransaction tx)
    {
        var head = Conn.QuerySingleOrDefault<(int Year, long? Income, int Pop, long Min)?>("SELECT year, expected_income_cents, pop_percent, pop_min_cents FROM budgets WHERE id = @id", new { id }, tx);
        if (head is not { } h) return null;
        var lines = Conn.Query<(long Category, long? Monthly, long? Annual)>("SELECT category_id, monthly_cents, annual_cents FROM budget_lines WHERE budget_id = @id ORDER BY category_id", new { id }, tx)
            .Select(l => new { categoryId = l.Category, monthlyCents = l.Monthly, annualCents = l.Annual }).ToList();
        return new { year = h.Year, expectedIncomeCents = h.Income, popPercent = h.Pop, popMinCents = h.Min, lines };
    }

    public void SaveBudget(int year, BudgetInput b) => InTransaction((tx, audit) =>
    {
        if (year is < 2000 or > 2100) throw Bad("That is not a sensible year.");
        if (b.ExpectedIncomeCents is < 0) throw Bad("Expected income cannot be negative.");
        if (b.PopPercent is < 0 or > 1000) throw Bad("The percent over has to be between 0 and 1000.");
        if (b.PopMinCents is < 0) throw Bad("The smallest amount over cannot be negative.");
        var seen = new HashSet<long>();
        foreach (var l in b.Lines ?? [])
        {
            if (!seen.Add(l.CategoryId)) throw Bad("A category can only be in the budget once.");
            if (!Exists("categories", l.CategoryId, tx)) throw Bad($"There is no category {l.CategoryId}.");
            if (l.MonthlyCents is < 0 || l.AnnualCents is < 0) throw Bad("A guideline cannot be negative.");
            if (l.MonthlyCents is not null && l.AnnualCents is not null) throw Bad("A category has a monthly guideline or an annual one, not both.");
        }

        var id = Conn.ExecuteScalar<long?>("SELECT id FROM budgets WHERE year = @year", new { year }, tx);
        var before = id is { } existing ? Budget(existing, tx) : null;
        if (id is null)
        {
            Conn.Execute("INSERT INTO budgets (year, expected_income_cents) VALUES (@year, NULL)", new { year }, tx);
            id = Conn.ExecuteScalar<long>(Sql.LastInsertId, transaction: tx);
        }
        Conn.Execute("""
            UPDATE budgets SET expected_income_cents = CASE WHEN @clear = 1 THEN NULL ELSE COALESCE(@income, expected_income_cents) END,
                pop_percent = COALESCE(@pop, pop_percent), pop_min_cents = COALESCE(@min, pop_min_cents)
            WHERE id = @id
            """, new { id, clear = b.ClearExpectedIncome ? 1 : 0, income = b.ExpectedIncomeCents, pop = b.PopPercent, min = b.PopMinCents }, tx);
        if (b.Lines is not null)
        {
            Conn.Execute("DELETE FROM budget_lines WHERE budget_id = @id", new { id }, tx);
            foreach (var l in b.Lines.Where(l => l.MonthlyCents is not null || l.AnnualCents is not null))
                Conn.Execute("INSERT INTO budget_lines (budget_id, category_id, monthly_cents, annual_cents) VALUES (@id, @category, @monthly, @annual)",
                    new { id, category = l.CategoryId, monthly = l.MonthlyCents, annual = l.AnnualCents }, tx);
        }
        audit.Write("budget.saved", "budget", id.Value, before, Budget(id.Value, tx));
    });
}
