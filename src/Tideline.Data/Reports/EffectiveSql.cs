namespace Tideline.Data.Reports;

/// <summary>
/// The SQL every report counts (docs/04-domain-rules.md, "Effective lines"; reference: docs/db/queries.sql).
/// <list type="bullet">
/// <item><c>effective</c>: each split line, plus the transaction's remainder (amount minus its splits). Moves produce none.</item>
/// <item><c>savings_legs</c>: the leg of a savings move that is not on a savings account, counted as type 'savings'.</item>
/// <item><c>move_legs</c>: every leg of every move, untyped.</item>
/// </list>
/// All three share one column list so they can be combined with UNION ALL.
/// </summary>
internal static class EffectiveSql
{
    public const string Columns =
        "transaction_id, split_line_id, account_id, posted_on, amount_cents, category_id, type, payee, description, notes, recurring_id, plan_id, move_id, is_split_part";

    /// <summary>A line the Bills grid has no row for: no recurring item, or an item whose category is not a bill.</summary>
    public const string OutsideBillRows =
        "e.recurring_id IS NULL OR NOT EXISTS (SELECT 1 FROM recurring ri JOIN categories rc ON rc.id = ri.category_id WHERE ri.id = e.recurring_id AND rc.default_type = 'bill')";

    public const string Ctes = """
        split_sums AS (
            SELECT transaction_id, SUM(amount_cents) AS s FROM split_lines GROUP BY transaction_id
        ),
        effective AS (
            SELECT t.id AS transaction_id, NULL AS split_line_id, t.account_id, t.posted_on,
                   t.amount_cents - COALESCE(ss.s, 0) AS amount_cents, t.category_id, t.type, t.payee,
                   t.original_description AS description, t.notes, t.recurring_id, t.plan_id, NULL AS move_id, 0 AS is_split_part
            FROM transactions t LEFT JOIN split_sums ss ON ss.transaction_id = t.id
            WHERE t.move_id IS NULL AND t.amount_cents - COALESCE(ss.s, 0) <> 0
            UNION ALL
            SELECT sl.transaction_id, sl.id, t.account_id, t.posted_on, sl.amount_cents, sl.category_id, sl.type, t.payee,
                   t.original_description, COALESCE(sl.note, t.notes), t.recurring_id, t.plan_id, NULL, 1
            FROM split_lines sl JOIN transactions t ON t.id = sl.transaction_id
        ),
        savings_legs AS (
            SELECT t.id AS transaction_id, NULL AS split_line_id, t.account_id, t.posted_on, t.amount_cents,
                   NULL AS category_id, 'savings' AS type, t.payee, t.original_description AS description, t.notes,
                   NULL AS recurring_id, t.plan_id, t.move_id, 0 AS is_split_part
            FROM transactions t JOIN moves m ON m.id = t.move_id JOIN accounts a ON a.id = t.account_id
            WHERE m.counts_as = 'savings' AND a.kind <> 'savings'
        ),
        move_legs AS (
            SELECT t.id AS transaction_id, NULL AS split_line_id, t.account_id, t.posted_on, t.amount_cents,
                   NULL AS category_id, NULL AS type, t.payee, t.original_description AS description, t.notes,
                   NULL AS recurring_id, t.plan_id, t.move_id, 0 AS is_split_part
            FROM transactions t WHERE t.move_id IS NOT NULL
        )
        """;
}
