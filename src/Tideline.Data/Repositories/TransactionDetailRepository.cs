using System.Data;
using Dapper;

namespace Tideline.Data.Repositories;

public sealed record SplitDetail(long Id, long AmountCents, long CategoryId, string CategoryName, string Type, string? Note, IReadOnlyList<string> Tags);

public sealed record MovePartner(long TransactionId, long AccountId, string AccountName, DateOnly PostedOn, long AmountCents, string Description);

public sealed record AuditEntry(long Id, DateTimeOffset At, string? User, string Action, string? BeforeJson, string? AfterJson);

/// <summary>Everything the transaction drawer shows (docs/08-screens.md, "Lines").</summary>
public sealed record TransactionDetail(
    long Id,
    long AccountId,
    string AccountName,
    DateOnly PostedOn,
    long AmountCents,
    string OriginalDescription,
    long? BankBalanceCents,
    string? CheckNumber,
    string ExternalId,
    string Source,
    string? RawJson,
    string? Payee,
    long? CategoryId,
    string? CategoryName,
    string? Type,
    IReadOnlyList<string> Tags,
    string? Notes,
    bool IsManualOverride,
    long? RuleId,
    string? RuleName,
    long? RecurringId,
    string? RecurringName,
    long? PlanId,
    long? AssetId,
    string? AssetName,
    long? MoveId,
    MovePartner? MovePartner,
    IReadOnlyList<SplitDetail> Splits,
    long UnassignedCents,
    string Version,
    IReadOnlyList<AuditEntry> Audit,
    string? LeftOutAt = null,
    string? LeftOutReason = null,
    long? LeftOutImportId = null);

public sealed class TransactionDetailRepository(IDbConnection conn, IDbTransaction? tx = null)
{
    private sealed class Row
    {
        public long Id { get; set; }
        public long AccountId { get; set; }
        public string AccountName { get; set; } = "";
        public DateOnly PostedOn { get; set; }
        public long AmountCents { get; set; }
        public string OriginalDescription { get; set; } = "";
        public long? BankBalanceCents { get; set; }
        public string? CheckNumber { get; set; }
        public string ExternalId { get; set; } = "";
        public string Source { get; set; } = "";
        public string? RawJson { get; set; }
        public string? Payee { get; set; }
        public long? CategoryId { get; set; }
        public string? CategoryName { get; set; }
        public string? Type { get; set; }
        public string? Notes { get; set; }
        public bool IsManualOverride { get; set; }
        public long? RuleId { get; set; }
        public string? RuleName { get; set; }
        public long? RecurringId { get; set; }
        public string? RecurringName { get; set; }
        public long? PlanId { get; set; }
        public long? AssetId { get; set; }
        public string? AssetName { get; set; }
        public long? MoveId { get; set; }
        public string UpdatedAt { get; set; } = "";
        public string? LeftOutAt { get; set; }
        public string? LeftOutReason { get; set; }
        public long? LeftOutImportId { get; set; }
    }

    private sealed class SplitRow
    {
        public long Id { get; set; }
        public long AmountCents { get; set; }
        public long CategoryId { get; set; }
        public string CategoryName { get; set; } = "";
        public string Type { get; set; } = "";
        public string? Note { get; set; }
    }

    private sealed class PartnerRow
    {
        public long TransactionId { get; set; }
        public long AccountId { get; set; }
        public string AccountName { get; set; } = "";
        public DateOnly PostedOn { get; set; }
        public long AmountCents { get; set; }
        public string Description { get; set; } = "";
    }

    private sealed class AuditRow
    {
        public long Id { get; set; }
        public DateTimeOffset At { get; set; }
        public string? UserName { get; set; }
        public string Action { get; set; } = "";
        public string? BeforeJson { get; set; }
        public string? AfterJson { get; set; }
    }

    public TransactionDetail? Get(long id)
    {
        TypeHandlers.EnsureRegistered();
        var t = conn.QuerySingleOrDefault<Row>("""
            SELECT t.id AS Id, t.account_id AS AccountId, a.name AS AccountName, t.posted_on AS PostedOn, t.amount_cents AS AmountCents,
                   t.original_description AS OriginalDescription, t.bank_balance_cents AS BankBalanceCents, t.check_number AS CheckNumber,
                   t.external_id AS ExternalId, t.source AS Source, t.raw_json AS RawJson, t.payee AS Payee,
                   t.category_id AS CategoryId, c.name AS CategoryName, t.type AS Type, t.notes AS Notes,
                   t.is_manual_override AS IsManualOverride, t.rule_id AS RuleId, r.name AS RuleName,
                   t.recurring_id AS RecurringId, rc.name AS RecurringName, t.plan_id AS PlanId, t.asset_account_id AS AssetId, aa.name AS AssetName, t.move_id AS MoveId,
                   t.updated_at AS UpdatedAt, t.left_out_at AS LeftOutAt, t.left_out_reason AS LeftOutReason, t.left_out_import_id AS LeftOutImportId
            FROM transactions t
            JOIN accounts a ON a.id = t.account_id
            LEFT JOIN accounts aa ON aa.id = t.asset_account_id
            LEFT JOIN categories c ON c.id = t.category_id
            LEFT JOIN rules r ON r.id = t.rule_id
            LEFT JOIN recurring rc ON rc.id = t.recurring_id
            WHERE t.id = @id
            """, new { id }, tx);
        if (t is null) return null;

        var tags = conn.Query<string>("""
            SELECT g.name FROM transaction_tags tt JOIN tags g ON g.id = tt.tag_id WHERE tt.transaction_id = @id ORDER BY g.name
            """, new { id }, tx).ToList();

        var splitRows = conn.Query<SplitRow>("""
            SELECT s.id AS Id, s.amount_cents AS AmountCents, s.category_id AS CategoryId, c.name AS CategoryName, s.type AS Type, s.note AS Note
            FROM split_lines s JOIN categories c ON c.id = s.category_id
            WHERE s.transaction_id = @id ORDER BY s.sort_order, s.id
            """, new { id }, tx).ToList();
        var splitTags = conn.Query<(long SplitId, string Name)>("""
            SELECT st.split_line_id, g.name FROM split_line_tags st JOIN tags g ON g.id = st.tag_id
            JOIN split_lines s ON s.id = st.split_line_id WHERE s.transaction_id = @id ORDER BY g.name
            """, new { id }, tx).ToLookup(x => x.SplitId, x => x.Name);
        var splits = splitRows.Select(s => new SplitDetail(s.Id, s.AmountCents, s.CategoryId, s.CategoryName, s.Type, s.Note, splitTags[s.Id].ToList())).ToList();

        MovePartner? partner = null;
        if (t.MoveId is { } moveId)
        {
            var p = conn.QuerySingleOrDefault<PartnerRow>("""
                SELECT x.id AS TransactionId, x.account_id AS AccountId, a.name AS AccountName, x.posted_on AS PostedOn,
                       x.amount_cents AS AmountCents, x.original_description AS Description
                FROM transactions x JOIN accounts a ON a.id = x.account_id
                WHERE x.move_id = @moveId AND x.id <> @id
                """, new { moveId, id }, tx);
            if (p is not null) partner = new MovePartner(p.TransactionId, p.AccountId, p.AccountName, p.PostedOn, p.AmountCents, p.Description);
        }

        var audit = conn.Query<AuditRow>("""
            SELECT l.id AS Id, l.at AS At, u.display_name AS UserName, l.action AS Action, l.before_json AS BeforeJson, l.after_json AS AfterJson
            FROM audit_log l LEFT JOIN users u ON u.id = l.user_id
            WHERE l.entity = 'transaction' AND l.entity_id = @key ORDER BY l.id DESC
            """, new { key = id.ToString() }, tx)
            .Select(a => new AuditEntry(a.Id, a.At, a.UserName, a.Action, a.BeforeJson, a.AfterJson)).ToList();

        return new TransactionDetail(t.Id, t.AccountId, t.AccountName, t.PostedOn, t.AmountCents, t.OriginalDescription, t.BankBalanceCents,
            t.CheckNumber, t.ExternalId, t.Source, t.RawJson, t.Payee, t.CategoryId, t.CategoryName, t.Type, tags, t.Notes, t.IsManualOverride,
            t.RuleId, t.RuleName, t.RecurringId, t.RecurringName, t.PlanId, t.AssetId, t.AssetName, t.MoveId, partner, splits,
            t.AmountCents - splits.Sum(s => s.AmountCents), Services.TransactionVersion.Of(conn, id, tx), audit, t.LeftOutAt, t.LeftOutReason, t.LeftOutImportId);
    }
}
