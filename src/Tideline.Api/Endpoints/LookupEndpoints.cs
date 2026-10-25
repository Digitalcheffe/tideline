using Dapper;
using Tideline.Data;
using Tideline.Data.Security;

namespace Tideline.Api.Endpoints;

/// <summary>Lists of the things a household is set up with. The Settings screens use the extra fields; everything else uses the first few.</summary>
public static class LookupEndpoints
{
    public static void MapLookupEndpoints(this RouteGroupBuilder api)
    {
        api.MapGet("/accounts", (SqliteConnectionFactory factory) =>
        {
            TypeHandlers.EnsureRegistered();
            using var conn = factory.Open();
            var cards = conn.Query<(long AccountId, bool Pay, int? Statement, int? Due, long? Limit)>(
                "SELECT account_id, is_pay_in_full, statement_day, due_day, credit_limit_cents FROM card_terms").ToDictionary(c => c.AccountId);

            var loans = conn.Query<(long AccountId, string Type, int? Rate, long? Payment, long? Escrow, int? Day, long? Original, DateOnly? Start, int? Term, long? Category, long? Secured)>(
                "SELECT account_id, loan_type, rate_bps, payment_cents, escrow_cents, payment_day, original_balance_cents, start_on, term_months, payment_category_id, secured_asset_id FROM loan_terms").ToDictionary(l => l.AccountId);
            var retirement = conn.Query<(long AccountId, string Type, long Employee, long Employer, bool Included, long? Pension)>(
                "SELECT account_id, account_type, employee_contribution_monthly_cents, employer_contribution_monthly_cents, is_included_in_goal, pension_annual_at_retirement_cents FROM retirement_terms").ToDictionary(r => r.AccountId);

            return Results.Ok(conn.Query<(long Id, string Name, string Kind, string? Institution, string? BankNumber, long? Opening, DateOnly? OpeningOn, bool InSpread, bool Closed,
                    DateOnly? CsvUntil, string? Notes, int Sort, int? Rate, string? SfinId, DateOnly? SfinFrom, long Lines)>("""
                SELECT a.id, a.name, a.kind, a.institution, a.bank_number_enc, a.opening_balance_cents, a.opening_on, a.is_in_spread, a.is_closed, a.csv_until_on, a.notes, a.sort_order, a.interest_rate_bps, a.simplefin_account_id, a.simplefin_from_on,
                       (SELECT COUNT(*) FROM transactions t WHERE t.account_id = a.id)
                FROM accounts a ORDER BY a.sort_order, a.id
                """).Select(a => new
            {
                a.Id, a.Name, kind = a.Kind, a.Institution, bankNumberMask = AccountNumbers.MaskFrom(a.BankNumber),
                openingBalanceCents = a.Opening, openingOn = a.OpeningOn, isInSpread = a.InSpread, isClosed = a.Closed, csvUntilOn = a.CsvUntil, a.Notes, sortOrder = a.Sort, interestRateBps = a.Rate, simplefinAccountId = a.SfinId, simplefinFromOn = a.SfinFrom, lineCount = a.Lines,
                loan = loans.TryGetValue(a.Id, out var l) ? new { loanType = l.Type, rateBps = l.Rate, paymentCents = l.Payment, escrowCents = l.Escrow, paymentDay = l.Day, originalBalanceCents = l.Original, startOn = l.Start, termMonths = l.Term, paymentCategoryId = l.Category, securedAssetId = l.Secured } : null,
                retirement = retirement.TryGetValue(a.Id, out var rt) ? new { accountType = rt.Type, employeeMonthlyCents = rt.Employee, employerMonthlyCents = rt.Employer, isIncludedInGoal = rt.Included, pensionAnnualAtRetirementCents = rt.Pension } : null,
                card = cards.TryGetValue(a.Id, out var c) ? new { isPayInFull = c.Pay, statementDay = c.Statement, dueDay = c.Due, creditLimitCents = c.Limit } : null,
            }));
        });

        api.MapGet("/move-patterns", (SqliteConnectionFactory factory) =>
        {
            using var conn = factory.Open();
            return Results.Ok(conn.Query<(long Id, long FromId, string FromName, string FromContains, long ToId, string ToName, string ToContains, int Days)>("""
                SELECT p.id, p.from_account_id, f.name, p.from_contains, p.to_account_id, t.name, p.to_contains, p.max_days_apart
                FROM move_patterns p JOIN accounts f ON f.id = p.from_account_id JOIN accounts t ON t.id = p.to_account_id ORDER BY p.id
                """).Select(p => new { p.Id, fromAccountId = p.FromId, fromAccountName = p.FromName, p.FromContains, toAccountId = p.ToId, toAccountName = p.ToName, p.ToContains, maxDaysApart = p.Days }));
        });

        api.MapGet("/categories", (bool? includeArchived, SqliteConnectionFactory factory) =>
        {
            using var conn = factory.Open();
            return Results.Ok(conn.Query<(long Id, string Name, string Type, bool Archived, int Sort, long? MergedInto, long Lines)>($"""
                SELECT c.id, c.name, c.default_type, c.is_archived, c.sort_order, c.merged_into_id,
                       (SELECT COUNT(*) FROM transactions t WHERE t.category_id = c.id) + (SELECT COUNT(*) FROM split_lines s WHERE s.category_id = c.id)
                FROM categories c {(includeArchived == true ? "" : "WHERE c.is_archived = 0")} ORDER BY c.sort_order, c.name
                """).Select(c => new { c.Id, c.Name, type = c.Type, isArchived = c.Archived, sortOrder = c.Sort, mergedIntoId = c.MergedInto, lineCount = c.Lines }));
        });

        api.MapGet("/tags", (SqliteConnectionFactory factory) =>
        {
            using var conn = factory.Open();
            return Results.Ok(conn.Query<(long Id, string Name, string? Color, long Lines)>("""
                SELECT g.id, g.name, g.color, (SELECT COUNT(*) FROM transaction_tags tt WHERE tt.tag_id = g.id) + (SELECT COUNT(*) FROM split_line_tags st WHERE st.tag_id = g.id)
                FROM tags g ORDER BY g.name
                """).Select(t => new { t.Id, t.Name, t.Color, lineCount = t.Lines }));
        });

        api.MapGet("/recurring", (SqliteConnectionFactory factory) =>
        {
            TypeHandlers.EnsureRegistered();
            using var conn = factory.Open();
            return Results.Ok(conn.Query<(long Id, string Name, long CategoryId, string CategoryName, string CategoryType, long Min, long Max, string Frequency, int EveryN, int DueDay,
                    DateOnly FirstDueOn, int Grace, long? AccountId, bool Active, DateOnly? EndedOn, long Lines)>("""
                SELECT r.id, r.name, r.category_id, c.name, c.default_type, r.expected_min_cents, r.expected_max_cents, r.frequency, r.every_n, r.due_day, r.first_due_on,
                       r.grace_days, r.account_id, r.is_active, r.ended_on, (SELECT COUNT(*) FROM transactions t WHERE t.recurring_id = r.id)
                FROM recurring r JOIN categories c ON c.id = r.category_id ORDER BY r.name
                """).Select(r => new
            {
                r.Id, r.Name, r.CategoryId, categoryName = r.CategoryName, categoryType = r.CategoryType, expectedMinCents = r.Min, expectedMaxCents = r.Max, r.Frequency,
                everyN = r.EveryN, dueDay = r.DueDay, firstDueOn = r.FirstDueOn, graceDays = r.Grace, accountId = r.AccountId, isActive = r.Active, endedOn = r.EndedOn, lineCount = r.Lines,
            }));
        });
    }
}
