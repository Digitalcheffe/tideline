using Dapper;
using Tideline.Core;
using Tideline.Data;
using Tideline.Data.Repositories;

namespace Tideline.Api.Endpoints;

public static class LookupEndpoints
{
    public static void MapLookupEndpoints(this RouteGroupBuilder api)
    {
        api.MapGet("/accounts", (SqliteConnectionFactory factory, TimeProvider time) =>
        {
            using var conn = factory.Open();
            return Results.Ok(new AccountRepository(conn, time).All().Select(a => new
            {
                a.Id, a.Name, kind = a.Kind.ToDb(), a.Institution, a.BankNumber, a.IsInSpread, a.IsClosed,
            }));
        });

        api.MapGet("/categories", (SqliteConnectionFactory factory) =>
        {
            using var conn = factory.Open();
            return Results.Ok(new CategoryRepository(conn).All().Where(c => !c.IsArchived)
                .Select(c => new { c.Id, c.Name, type = c.DefaultType.ToDb() }));
        });

        api.MapGet("/tags", (SqliteConnectionFactory factory) =>
        {
            using var conn = factory.Open();
            return Results.Ok(conn.Query<(long Id, string Name)>("SELECT id, name FROM tags ORDER BY name").Select(t => new { t.Id, t.Name }));
        });

        api.MapGet("/recurring", (SqliteConnectionFactory factory) =>
        {
            using var conn = factory.Open();
            return Results.Ok(new RecurringRepository(conn).All().Select(r => new
            {
                r.Item.Id, r.Item.Name, r.Item.CategoryId, categoryName = r.CategoryName, categoryType = r.CategoryType.ToDb(),
            }));
        });
    }
}
