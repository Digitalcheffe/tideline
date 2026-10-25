using System.Data;
using Dapper;
using Tideline.Data.Repositories;

namespace Tideline.Data.Services;

public sealed record SnapshotInput(string? AsOn, long? BalanceCents, string? Source = null, string? Note = null);

public sealed record PersonInput(string? Name, string? BirthOn = null, int? RetireAge = null, long? SocialSecurityAnnualTodayCents = null, bool? IsActive = null, bool ClearBirth = false);

public sealed record GoalInput(
    long? GoalAnnualIncomeTodayCents = null,
    long? OtherIncomeAnnualTodayCents = null,
    int? ReturnBeforeBps = null,
    int? ReturnDuringBps = null,
    int? InflationBps = null,
    int? WithdrawalRateBps = null,
    int? PlanToAge = null,
    bool ClearGoal = false);

/// <summary>
/// The figures a person enters themselves: what a loan, a house or a retirement account is worth on a day, the people the retirement
/// model is about, and the goal and assumptions. Each change is audited with before and after.
/// </summary>
public sealed class FinancialEditor(IDbConnection conn, TimeProvider clock, Actor? actor = null) : EditorBase(conn, clock, actor)
{
    private static readonly string[] SnapshotKinds = ["loan", "asset", "retirement"];

    // ---- balances you type in -----------------------------------------------------------------------------------

    public long SaveSnapshot(long accountId, SnapshotInput s) => InTransaction((tx, audit) =>
    {
        var kind = Conn.ExecuteScalar<string?>("SELECT kind FROM accounts WHERE id = @accountId", new { accountId }, tx) ?? throw Missing("account");
        if (!SnapshotKinds.Contains(kind)) throw Bad("Balances are typed in for loans, assets and retirement accounts. The others come from the bank.");
        var day = Day(s.AsOn, "The date") ?? throw Bad("Say which day the balance is for.");
        if (day > DateOnly.FromDateTime(Clock.GetUtcNow().UtcDateTime).AddDays(1)) throw Bad("A balance cannot be for a day that has not happened yet.");
        if (s.BalanceCents is null or < 0) throw Bad(kind == "loan" ? "Enter what is owed, as zero or more." : "Enter the value, as zero or more.");
        var balance = s.BalanceCents.Value;
        var source = (s.Source ?? "statement").Trim().ToLowerInvariant();
        if (source is not ("statement" or "estimate")) throw Bad("A balance you type in is a statement figure or an estimate.");
        var note = Blank(s.Note);
        if (note is { Length: > 400 }) throw Bad("The note is too long (400 characters at most).");

        var before = Conn.QuerySingleOrDefault<(long Balance, string? Note)?>("SELECT balance_cents, note FROM balance_snapshots WHERE account_id = @accountId AND as_of_on = @day AND source = @source",
            new { accountId, day, source }, tx);
        Conn.Execute("DELETE FROM balance_snapshots WHERE account_id = @accountId AND as_of_on = @day AND source = @source", new { accountId, day, source }, tx);
        Conn.Execute("""
            INSERT INTO balance_snapshots (account_id, as_of_on, balance_cents, source, note, created_by, created_at)
            VALUES (@accountId, @day, @balance, @source, @note, @by, @now)
            """, new { accountId, day, balance, source, note, by = UserId, now = Clock.GetUtcNow() }, tx);
        var id = Conn.ExecuteScalar<long>(Sql.LastInsertId, transaction: tx);
        audit.Write("snapshot.saved", "account", accountId, before is { } b ? new { asOn = day, balanceCents = b.Balance, source, note = b.Note } : null, new { asOn = day, balanceCents = balance, source, note });
        LoanTracker.Refresh(Conn, Clock, tx);
        return id;
    });

    public void DeleteSnapshot(long id) => InTransaction((tx, audit) =>
    {
        var row = Conn.QuerySingleOrDefault<(long AccountId, DateOnly AsOn, long Balance, string Source)?>(
            "SELECT account_id, as_of_on, balance_cents, source FROM balance_snapshots WHERE id = @id", new { id }, tx) ?? throw Missing("balance");
        if (row.Source is not ("statement" or "estimate")) throw Bad("Only a balance you typed in can be removed.");
        Conn.Execute("DELETE FROM balance_snapshots WHERE id = @id", new { id }, tx);
        audit.Write("snapshot.deleted", "account", row.AccountId, new { asOn = row.AsOn, balanceCents = row.Balance, source = row.Source }, null);
        LoanTracker.Refresh(Conn, Clock, tx);
    });

    // ---- people ---------------------------------------------------------------------------------------------------

    private object? Person(long id, IDbTransaction tx) => Conn.QuerySingleOrDefault<(string Name, string? Birth, int? Age, long? Ss, bool Active)?>(
        "SELECT name, birth_on, retire_age, social_security_annual_today_cents, is_active FROM people WHERE id = @id", new { id }, tx) is { } p
        ? new { name = p.Name, birthOn = p.Birth, retireAge = p.Age, socialSecurityAnnualTodayCents = p.Ss, isActive = p.Active }
        : null;

    private static void CheckPerson(PersonInput p, DateOnly? birth)
    {
        if (birth is { } b && (b.Year < 1900 || b > DateOnly.FromDateTime(DateTime.UtcNow))) throw Bad("The birth date is not a sensible one.");
        if (p.RetireAge is < 30 or > 100) throw Bad("The retirement age must be between 30 and 100.");
        if (p.SocialSecurityAnnualTodayCents is < 0) throw Bad("The Social Security estimate cannot be negative.");
    }

    public long CreatePerson(PersonInput p) => InTransaction((tx, audit) =>
    {
        var name = Name(p.Name, 100, "person");
        var birth = Day(p.BirthOn, "The birth date");
        CheckPerson(p, birth);
        Conn.Execute("INSERT INTO people (name, birth_on, retire_age, social_security_annual_today_cents, is_active) VALUES (@name, @birth, @age, @ss, 1)",
            new { name, birth, age = p.RetireAge, ss = p.SocialSecurityAnnualTodayCents }, tx);
        var id = Conn.ExecuteScalar<long>(Sql.LastInsertId, transaction: tx);
        audit.Write("person.created", "person", id, null, Person(id, tx));
        return id;
    });

    public void UpdatePerson(long id, PersonInput p) => InTransaction((tx, audit) =>
    {
        var current = Conn.QuerySingleOrDefault<(string Name, string? Birth, int? Age, long? Ss, bool Active)?>(
            "SELECT name, birth_on, retire_age, social_security_annual_today_cents, is_active FROM people WHERE id = @id", new { id }, tx) ?? throw Missing("person");
        var before = Person(id, tx);
        var name = p.Name is null ? current.Name : Name(p.Name, 100, "person");
        var birth = p.ClearBirth ? null : p.BirthOn is null ? Day(current.Birth, "The birth date") : Day(p.BirthOn, "The birth date");
        CheckPerson(p, birth);
        Conn.Execute("UPDATE people SET name = @name, birth_on = @birth, retire_age = @age, social_security_annual_today_cents = @ss, is_active = @active WHERE id = @id", new
        {
            id, name, birth, age = p.RetireAge ?? current.Age, ss = p.SocialSecurityAnnualTodayCents ?? current.Ss, active = (p.IsActive ?? current.Active) ? 1 : 0,
        }, tx);
        audit.Write("person.updated", "person", id, before, Person(id, tx));
    });

    // ---- the retirement goal and its assumptions -----------------------------------------------------------------------

    private object? Goal(IDbTransaction tx) => Conn.QuerySingleOrDefault<(long? Goal, long Other, int Before, int During, int Inflation, int Withdrawal, int PlanTo)?>(
        "SELECT goal_annual_income_today_cents, other_income_annual_today_cents, return_before_bps, return_during_bps, inflation_bps, withdrawal_rate_bps, plan_to_age FROM retirement_goal WHERE id = 1", transaction: tx) is { } g
        ? new { goalAnnualIncomeTodayCents = g.Goal, otherIncomeAnnualTodayCents = g.Other, returnBeforeBps = g.Before, returnDuringBps = g.During, inflationBps = g.Inflation, withdrawalRateBps = g.Withdrawal, planToAge = g.PlanTo }
        : null;

    public void SaveGoal(GoalInput g) => InTransaction((tx, audit) =>
    {
        if (g.GoalAnnualIncomeTodayCents is < 0 || g.OtherIncomeAnnualTodayCents is < 0) throw Bad("Incomes cannot be negative.");
        foreach (var (bps, what) in new[] { (g.ReturnBeforeBps, "return before retirement"), (g.ReturnDuringBps, "return in retirement"), (g.InflationBps, "inflation") })
            if (bps is < 0 or > 2500) throw Bad($"The {what} must be between 0% and 25%.");
        if (g.WithdrawalRateBps is < 100 or > 2000) throw Bad("The withdrawal rate must be between 1% and 20%.");
        if (g.PlanToAge is < 60 or > 120) throw Bad("Plan to an age between 60 and 120.");

        var before = Goal(tx);
        var existing = before is not null;
        if (!existing) Conn.Execute("INSERT INTO retirement_goal (id, updated_at) VALUES (1, @now)", new { now = Clock.GetUtcNow() }, tx);
        Conn.Execute("""
            UPDATE retirement_goal SET
                goal_annual_income_today_cents = CASE WHEN @clear = 1 THEN NULL ELSE COALESCE(@goal, goal_annual_income_today_cents) END,
                other_income_annual_today_cents = COALESCE(@other, other_income_annual_today_cents),
                return_before_bps = COALESCE(@before, return_before_bps), return_during_bps = COALESCE(@during, return_during_bps),
                inflation_bps = COALESCE(@inflation, inflation_bps), withdrawal_rate_bps = COALESCE(@withdrawal, withdrawal_rate_bps),
                plan_to_age = COALESCE(@planTo, plan_to_age), updated_at = @now
            WHERE id = 1
            """, new
        {
            clear = g.ClearGoal ? 1 : 0, goal = g.GoalAnnualIncomeTodayCents, other = g.OtherIncomeAnnualTodayCents, before = g.ReturnBeforeBps, during = g.ReturnDuringBps,
            inflation = g.InflationBps, withdrawal = g.WithdrawalRateBps, planTo = g.PlanToAge, now = Clock.GetUtcNow(),
        }, tx);
        audit.Write("retirement_goal.saved", "retirement_goal", 1, before, Goal(tx));
    });
}
