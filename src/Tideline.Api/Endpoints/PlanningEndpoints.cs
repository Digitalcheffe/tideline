using Dapper;
using Tideline.Data;
using Tideline.Data.Reports;
using Tideline.Data.Services;

namespace Tideline.Api.Endpoints;

/// <summary>Net worth, retirement, plans, budgets and savings (docs/07-api.md). Reads need any signed-in person; changes need a member, or the owner for account figures.</summary>
public static class PlanningEndpoints
{
    public sealed record GoalRequest(GoalInput? Goal);

    public static void MapPlanningEndpoints(this RouteGroupBuilder api)
    {
        // ---- net worth and debt ----
        api.MapGet("/net-worth", (SqliteConnectionFactory f, Clock clock) =>
        {
            using var conn = f.Open();
            return Results.Ok(new NetWorthReader(conn).Read(clock.Today));
        });

        api.MapGet("/loans/{id:long}/countdown", (long id, long? extraCents, SqliteConnectionFactory f, Clock clock) =>
        {
            using var conn = f.Open();
            return new NetWorthReader(conn).Countdown(id, extraCents ?? 0, clock.Today) is { } effect
                ? Results.Ok(new
                {
                    extraCents = extraCents ?? 0,
                    without = Describe(effect.Without), with = Describe(effect.With),
                    monthsSaved = double.IsInfinity(effect.MonthsSaved) ? (double?)null : Math.Round(effect.MonthsSaved, 2), interestSavedCents = effect.InterestSavedCents,
                })
                : Results.Problem(title: "Not enough to work with", detail: "Enter the balance, the rate and the monthly payment for this loan first.", statusCode: 422);
        });

        // balances typed in for loans, assets and retirement accounts (owner only, because it is under /accounts)
        api.MapGet("/accounts/{id:long}/snapshots", (long id, SqliteConnectionFactory f) =>
        {
            TypeHandlers.EnsureRegistered();
            using var conn = f.Open();
            return Results.Ok(conn.Query<(long Id, DateOnly AsOn, long Cents, string Source, string? Note)>(
                    "SELECT id, as_of_on, balance_cents, source, note FROM balance_snapshots WHERE account_id = @id ORDER BY as_of_on DESC, id DESC LIMIT 100", new { id })
                .Select(s => new { s.Id, asOn = s.AsOn, balanceCents = s.Cents, s.Source, s.Note }));
        });
        api.MapPost("/accounts/{id:long}/snapshots", (long id, SnapshotInput input, HttpContext http, SqliteConnectionFactory f, TimeProvider t) =>
            Edits.Run(http, f, t, (c, clock, who) => new { id = new FinancialEditor(c, clock, who).SaveSnapshot(id, input) }, created: true));
        api.MapDelete("/accounts/{id:long}/snapshots/{snapshotId:long}", (long id, long snapshotId, HttpContext http, SqliteConnectionFactory f, TimeProvider t) =>
            Edits.Run(http, f, t, (c, clock, who) => { new FinancialEditor(c, clock, who).DeleteSnapshot(snapshotId); return new { deleted = true }; }));

        // ---- retirement ----
        api.MapGet("/retirement", (SqliteConnectionFactory f, Clock clock) =>
        {
            using var conn = f.Open();
            return Results.Ok(new RetirementReader(conn).Read(clock.Today));
        });
        api.MapGet("/retirement/goal", (SqliteConnectionFactory f) =>
        {
            using var conn = f.Open();
            return Results.Ok(new RetirementReader(conn).Goal());
        });
        api.MapPut("/retirement/goal", (GoalInput input, HttpContext http, SqliteConnectionFactory f, TimeProvider t) =>
            Edits.Run(http, f, t, (c, clock, who) => { new FinancialEditor(c, clock, who).SaveGoal(input); return new RetirementReader(c).Goal(); }));

        api.MapGet("/people", (SqliteConnectionFactory f, Clock clock) =>
        {
            using var conn = f.Open();
            return Results.Ok(new RetirementReader(conn).Read(clock.Today).People);
        });
        api.MapPost("/people", (PersonInput input, HttpContext http, SqliteConnectionFactory f, TimeProvider t) =>
            Edits.Run(http, f, t, (c, clock, who) => new { id = new FinancialEditor(c, clock, who).CreatePerson(input) }, created: true));
        api.MapPatch("/people/{id:long}", (long id, PersonInput input, HttpContext http, SqliteConnectionFactory f, TimeProvider t) =>
            Edits.Run(http, f, t, (c, clock, who) => { new FinancialEditor(c, clock, who).UpdatePerson(id, input); return new { updated = true }; }));

        // ---- plans ----
        api.MapGet("/plans", (bool? includeCancelled, SqliteConnectionFactory f, Clock clock) =>
        {
            using var conn = f.Open();
            return Results.Ok(new PlanReader(conn).Read(clock.Today, includeCancelled == true));
        });
        api.MapPost("/plans", (PlanInput input, HttpContext http, SqliteConnectionFactory f, TimeProvider t) =>
            Edits.Run(http, f, t, (c, clock, who) => new { id = new PlanEditor(c, clock, who).CreatePlan(input) }, created: true));
        api.MapPatch("/plans/{id:long}", (long id, PlanInput input, HttpContext http, SqliteConnectionFactory f, TimeProvider t) =>
            Edits.Run(http, f, t, (c, clock, who) => { new PlanEditor(c, clock, who).UpdatePlan(id, input); return new { updated = true }; }));

        // ---- budget ----
        api.MapGet("/budgets/{year:int}", (int year, SqliteConnectionFactory f, Clock clock) =>
        {
            using var conn = f.Open();
            var view = new BudgetReader(conn).Read(year, clock.Today);
            var from = new DateOnly(view.Year, 1, 1);
            var monthFrom = new DateOnly(view.Year, int.Parse(view.Month[5..7]), 1);
            var ytdTo = view.Year == clock.Today.Year ? clock.Today : new DateOnly(view.Year, 12, 31);
            var monthTo = view.Year == clock.Today.Year ? clock.Today : monthFrom.AddMonths(1).AddDays(-1);
            const string Types = "bill,required,extra,one_time";
            return Results.Ok(new
            {
                view.Year, view.Month, view.ElapsedMonths, view.PopPercent, view.PopMinCents, view.Saved,
                income = new
                {
                    expectedAnnualCents = view.Income.ExpectedAnnualCents, expectedYtdCents = view.Income.ExpectedYtdCents, @short = view.Income.Short,
                    actualYtd = new Figure(view.Income.ActualYtdCents, Drill.Range(from, ytdTo, ("type", "income"))),
                },
                categories = view.Categories.Select(c => new
                {
                    c.CategoryId, c.Name, c.Type, c.MonthlyCents, c.AnnualCents, c.Popped,
                    thisMonth = new Figure(c.ThisMonthCents, c.ThisMonthCents == 0 ? null : Drill.Range(monthFrom, monthTo, ("type", Types), ("category", c.CategoryId))),
                    ytd = new Figure(c.YtdCents, c.YtdCents == 0 ? null : Drill.Range(from, ytdTo, ("type", Types), ("category", c.CategoryId))),
                }),
            });
        });
        api.MapPut("/budgets/{year:int}", (int year, BudgetInput input, HttpContext http, SqliteConnectionFactory f, TimeProvider t) =>
            Edits.Run(http, f, t, (c, clock, who) => { new PlanEditor(c, clock, who).SaveBudget(year, input); return new { saved = true }; }));
        // only works a proposal out; saving it is the PUT above, after a person has looked
        api.MapPost("/budgets/{year:int}/fill-from-averages", (int year, SqliteConnectionFactory f, Clock clock) =>
        {
            using var conn = f.Open();
            return Results.Ok(new BudgetReader(conn).ProposeFromAverages(year, clock.Today));
        });

        // ---- savings ----
        api.MapGet("/savings", (SqliteConnectionFactory f, Clock clock) =>
        {
            using var conn = f.Open();
            var view = new SavingsReader(conn).Read(clock.Today);
            return Results.Ok(new
            {
                view.TotalBalanceCents, view.TotalAverageMonthlyDepositCents,
                accounts = view.Accounts.Select(a => new
                {
                    a.AccountId, a.Name, a.BalanceCents, a.RateBps, a.AverageMonthlyDepositCents, a.InterestPerMonthCents,
                    deposits = a.Deposits.Select(d => new
                    {
                        d.Month, cents = d.Cents,
                        drill = d.Cents == 0 ? null : Drill.Of(("from", $"{d.Month}-01"), ("to", $"{d.Month}-{DateTime.DaysInMonth(int.Parse(d.Month[..4]), int.Parse(d.Month[5..7])):00}"), ("account", a.AccountId), ("direction", "in"), ("move", "include")),
                    }),
                }),
                view.Goals,
            });
        });
    }

    private static object Describe(Tideline.Core.Payoff p) => new
    {
        paysOff = p.PaysOff, monthsLeft = p.PaysOff ? Math.Round(p.MonthsLeft, 2) : (double?)null, interestLeftCents = p.InterestLeftCents, payoffMonth = p.PayoffMonth,
    };
}
