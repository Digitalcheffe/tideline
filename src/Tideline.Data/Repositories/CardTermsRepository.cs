using System.Data;
using Dapper;

namespace Tideline.Data.Repositories;

public sealed record CardTerms(long AccountId, bool IsPayInFull, int? StatementDay, int? DueDay, long? CreditLimitCents);

public sealed class CardTermsRepository(IDbConnection conn, IDbTransaction? tx = null)
{
    private sealed class Row
    {
        public long AccountId { get; set; }
        public bool IsPayInFull { get; set; }
        public int? StatementDay { get; set; }
        public int? DueDay { get; set; }
        public long? CreditLimitCents { get; set; }
    }

    public void Upsert(CardTerms t)
    {
        conn.Execute("DELETE FROM card_terms WHERE account_id = @AccountId", new { t.AccountId }, tx);
        conn.Execute("""
            INSERT INTO card_terms (account_id, is_pay_in_full, statement_day, due_day, credit_limit_cents)
            VALUES (@AccountId, @IsPayInFull, @StatementDay, @DueDay, @CreditLimitCents)
            """, new { t.AccountId, IsPayInFull = t.IsPayInFull ? 1 : 0, t.StatementDay, t.DueDay, t.CreditLimitCents }, tx);
    }

    public IReadOnlyDictionary<long, CardTerms> All() =>
        conn.Query<Row>("""
            SELECT account_id AS AccountId, is_pay_in_full AS IsPayInFull, statement_day AS StatementDay, due_day AS DueDay,
                   credit_limit_cents AS CreditLimitCents
            FROM card_terms
            """, transaction: tx)
            .ToDictionary(r => r.AccountId, r => new CardTerms(r.AccountId, r.IsPayInFull, r.StatementDay, r.DueDay, r.CreditLimitCents));
}
