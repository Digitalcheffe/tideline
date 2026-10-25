using System.Data;
using Dapper;
using Tideline.Core;

namespace Tideline.Data.Repositories;

public sealed record NewRecurring(
    string Name,
    long CategoryId,
    long ExpectedMinCents,
    long ExpectedMaxCents,
    Frequency Frequency,
    int DueDay,
    DateOnly FirstDueOn,
    int EveryN = 1,
    int GraceDays = 5,
    long? AccountId = null,
    bool IsActive = true,
    DateOnly? EndedOn = null);

/// <summary>A recurring item with the default type of its category (bills have category type 'bill').</summary>
public sealed record RecurringWithType(RecurringItem Item, LineType CategoryType, string CategoryName);

public sealed class RecurringRepository(IDbConnection conn, IDbTransaction? tx = null)
{
    private sealed class Row
    {
        public long Id { get; set; }
        public string Name { get; set; } = "";
        public long CategoryId { get; set; }
        public long ExpectedMinCents { get; set; }
        public long ExpectedMaxCents { get; set; }
        public string Frequency { get; set; } = "";
        public int EveryN { get; set; }
        public int DueDay { get; set; }
        public DateOnly FirstDueOn { get; set; }
        public int GraceDays { get; set; }
        public bool IsActive { get; set; }
        public DateOnly? EndedOn { get; set; }
        public string CategoryType { get; set; } = "";
        public string CategoryName { get; set; } = "";
    }

    public long Insert(NewRecurring r)
    {
        TypeHandlers.EnsureRegistered();
        conn.Execute("""
            INSERT INTO recurring (name, category_id, expected_min_cents, expected_max_cents, frequency, every_n, due_day, first_due_on,
                                   grace_days, account_id, is_active, ended_on)
            VALUES (@Name, @CategoryId, @ExpectedMinCents, @ExpectedMaxCents, @Frequency, @EveryN, @DueDay, @FirstDueOn,
                    @GraceDays, @AccountId, @IsActive, @EndedOn)
            """, new
        {
            r.Name, r.CategoryId, r.ExpectedMinCents, r.ExpectedMaxCents, Frequency = r.Frequency.ToDb(), r.EveryN, r.DueDay,
            r.FirstDueOn, r.GraceDays, r.AccountId, IsActive = r.IsActive ? 1 : 0, r.EndedOn,
        }, tx);
        return conn.ExecuteScalar<long>(Sql.LastInsertId, transaction: tx);
    }

    public long? IdByName(string name) => conn.ExecuteScalar<long?>("SELECT id FROM recurring WHERE name = @name", new { name }, tx);

    public IReadOnlyList<RecurringWithType> All()
    {
        TypeHandlers.EnsureRegistered();
        return conn.Query<Row>("""
            SELECT r.id AS Id, r.name AS Name, r.category_id AS CategoryId, r.expected_min_cents AS ExpectedMinCents,
                   r.expected_max_cents AS ExpectedMaxCents, r.frequency AS Frequency, r.every_n AS EveryN, r.due_day AS DueDay,
                   r.first_due_on AS FirstDueOn, r.grace_days AS GraceDays, r.is_active AS IsActive, r.ended_on AS EndedOn,
                   c.default_type AS CategoryType, c.name AS CategoryName
            FROM recurring r JOIN categories c ON c.id = r.category_id
            ORDER BY r.name
            """, transaction: tx)
            .Select(r => new RecurringWithType(
                new RecurringItem(r.Id, r.Name, r.CategoryId, r.ExpectedMinCents, r.ExpectedMaxCents,
                    TypeHandlers.FromDb<Frequency>(r.Frequency), r.EveryN, r.DueDay, r.FirstDueOn, r.GraceDays, r.IsActive, r.EndedOn),
                TypeHandlers.FromDb<LineType>(r.CategoryType), r.CategoryName)).ToList();
    }
}
