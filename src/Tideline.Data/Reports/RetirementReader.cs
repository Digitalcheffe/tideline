using System.Data;
using Dapper;
using Tideline.Core;
using Tideline.Data.Repositories;
using Tideline.Data.Services;

namespace Tideline.Data.Reports;

public sealed record RetirementPersonView(long Id, string Name, string? BirthOn, int? RetireAge, long SocialSecurityAnnualTodayCents, bool IsActive, string? RetirementDate);

public sealed record RetirementAccountView(
    long AccountId, string Name, string AccountType, long? BalanceCents, string? AsOf, bool Stale,
    long EmployeeMonthlyCents, long EmployerMonthlyCents, bool IsIncluded, long? PensionAnnualAtRetirementCents);

public sealed record AssumptionsView(
    long? GoalAnnualIncomeTodayCents, long OtherIncomeAnnualTodayCents, int ReturnBeforeBps, int ReturnDuringBps, int InflationBps, int WithdrawalRateBps, int PlanToAge);

public sealed record ChartPoint(int Year, long BalanceCents);

public sealed record RetirementView(
    bool Ready,
    IReadOnlyList<string> Missing,
    IReadOnlyList<RetirementPersonView> People,
    IReadOnlyList<RetirementAccountView> Accounts,
    AssumptionsView Assumptions,
    string? RetirementDate,
    int? MonthsToRetirement,
    RetirementResult? Result,
    int? RetireAtAge,
    IReadOnlyList<ChartPoint> Chart,
    long SocialSecurityTotalAnnualTodayCents);

/// <summary>Gathers what a person has typed in and runs the retirement model over it (docs/04-domain-rules.md, "Retirement model").</summary>
public sealed class RetirementReader(IDbConnection conn)
{
    public const int StaleDays = 180;

    public static readonly AssumptionsView Defaults = new(null, 0, 600, 400, 300, 400, 95);

    public AssumptionsView Goal()
    {
        TypeHandlers.EnsureRegistered();
        var g = conn.QuerySingleOrDefault<(long? Goal, long Other, int Before, int During, int Inflation, int Withdrawal, int PlanTo)?>(
            "SELECT goal_annual_income_today_cents, other_income_annual_today_cents, return_before_bps, return_during_bps, inflation_bps, withdrawal_rate_bps, plan_to_age FROM retirement_goal WHERE id = 1");
        return g is { } v ? new AssumptionsView(v.Goal, v.Other, v.Before, v.During, v.Inflation, v.Withdrawal, v.PlanTo) : Defaults;
    }

    public RetirementView Read(DateOnly today)
    {
        TypeHandlers.EnsureRegistered();
        var goal = Goal();

        var people = conn.Query<(long Id, string Name, DateOnly? Birth, int? Age, long? Ss, bool Active)>(
                "SELECT id, name, birth_on, retire_age, social_security_annual_today_cents, is_active FROM people ORDER BY id")
            .Select(p => new RetirementPersonView(p.Id, p.Name, p.Birth?.ToString("yyyy-MM-dd"), p.Age, p.Ss ?? 0, p.Active,
                p.Birth is { } b && p.Age is { } a ? b.AddYears(a).ToString("yyyy-MM-dd") : null)).ToList();
        var active = people.Where(p => p.IsActive).ToList();

        var accounts = new List<RetirementAccountView>();
        foreach (var r in conn.Query<(long Id, string Name, string Type, long Employee, long Employer, bool Included, long? Pension)>("""
                    SELECT a.id, a.name, t.account_type, t.employee_contribution_monthly_cents, t.employer_contribution_monthly_cents, t.is_included_in_goal, t.pension_annual_at_retirement_cents
                    FROM accounts a JOIN retirement_terms t ON t.account_id = a.id WHERE a.kind = 'retirement' AND a.is_closed = 0 ORDER BY a.sort_order, a.id
                    """))
        {
            var snap = conn.QueryFirstOrDefault<(DateOnly AsOn, long Cents, string Source)?>($"""
                SELECT as_of_on, balance_cents, source FROM balance_snapshots WHERE account_id = @id AND as_of_on <= @today
                ORDER BY as_of_on DESC, {LoanTracker.PriorityOrderSql} LIMIT 1
                """, new { id = r.Id, today });
            accounts.Add(new RetirementAccountView(r.Id, r.Name, r.Type, snap?.Cents, snap?.AsOn.ToString("yyyy-MM-dd"),
                snap is { } s && today.DayNumber - s.AsOn.DayNumber > StaleDays, r.Employee, r.Employer, r.Included, r.Pension));
        }

        var missing = new List<string>();
        var core = active.Select(p => new RetirementPerson(p.Name, p.BirthOn is null ? null : DateOnly.Parse(p.BirthOn), p.RetireAge, p.SocialSecurityAnnualTodayCents)).ToList();
        var date = RetirementModel.RetirementDate(core);
        if (active.Count == 0) missing.Add("Add each person the plan is for: a name, a birth date and the age they want to retire.");
        else foreach (var p in active.Where(p => p.BirthOn is null || p.RetireAge is null)) missing.Add($"Enter {p.Name}'s birth date and the age they want to retire.");
        if (goal.GoalAnnualIncomeTodayCents is null) missing.Add("Enter the yearly income you want in retirement, in today's dollars.");
        if (accounts.Count == 0) missing.Add("Add a retirement account (Settings, Money accounts, kind Retirement).");
        foreach (var a in accounts.Where(a => a.BalanceCents is null && a.AccountType != "pension")) missing.Add($"Enter the balance of {a.Name}.");

        var ss = active.Sum(p => p.SocialSecurityAnnualTodayCents);
        RetirementResult? result = null;
        int? retireAt = null;
        var chart = new List<ChartPoint>();
        int? months = date is { } d ? RetirementModel.WholeMonths(today, d) : null;

        if (missing.Count == 0 && months is { } m)
        {
            var inputs = accounts.Select(a => new RetirementAccountInput(a.Name, a.BalanceCents ?? 0, a.EmployeeMonthlyCents + a.EmployerMonthlyCents, a.IsIncluded, a.PensionAnnualAtRetirementCents ?? 0)).ToList();
            var assumptions = new RetirementAssumptions(goal.GoalAnnualIncomeTodayCents!.Value, goal.OtherIncomeAnnualTodayCents, goal.ReturnBeforeBps, goal.ReturnDuringBps, goal.InflationBps, goal.WithdrawalRateBps, goal.PlanToAge);
            result = RetirementModel.Compute(m, inputs, ss, assumptions);
            retireAt = RetirementModel.RetireAtAge(today, core, inputs, assumptions);
            chart = RetirementModel.ByYear(m, inputs, goal.ReturnBeforeBps).Select(p => new ChartPoint(p.Year, p.BalanceCents)).ToList();
        }

        return new RetirementView(missing.Count == 0 && result is not null, missing, people, accounts, goal, date?.ToString("yyyy-MM-dd"), months, result, retireAt, chart, ss);
    }
}
