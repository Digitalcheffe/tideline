using Tideline.Data;
using Tideline.Data.Reports;

namespace Tideline.Api.Endpoints;

/// <summary>Asset ledgers (docs/ARCHITECTURE.md): what each asset is worth and what has been put into it. Reads only; lines are linked with PATCH /transactions.</summary>
public static class AssetEndpoints
{
    public static void MapAssetEndpoints(this RouteGroupBuilder api)
    {
        api.MapGet("/assets", (SqliteConnectionFactory f, Clock clock) =>
        {
            using var conn = f.Open();
            return Results.Ok(new AssetReader(conn).Read(clock.Today).Select(a => Describe(a, clock.Today)));
        });

        api.MapGet("/assets/{id:long}", (long id, SqliteConnectionFactory f, Clock clock) =>
        {
            using var conn = f.Open();
            return new AssetReader(conn).Get(id, clock.Today) is { } asset
                ? Results.Ok(Describe(asset, clock.Today))
                : Results.Problem(title: "Not found", detail: $"There is no asset {id}.", statusCode: 404);
        });
    }

    private static object Describe(AssetView a, DateOnly today)
    {
        var from = a.FirstLineOn is { } f ? DateOnly.Parse(f) : today;
        var to = a.LastLineOn is { } l ? DateOnly.Parse(l) : today;
        string Range(params (string, object?)[] more) => Drill.Range(from, to, [("asset", a.AssetId), .. more]);

        return new
        {
            a.AssetId, a.Name, a.IsClosed,
            a.ValueCents, a.ValueAsOf, a.ValueStale, a.OwedCents, a.EquityCents, a.Loans,
            a.PutInCents, a.Lines, drill = a.Lines > 0 ? Range() : null,
            a.FirstValueCents, a.FirstValueAsOf, a.ValueChangeCents, a.NetCents,
            byYear = a.ByYear.Select(y => new
            {
                y.Year, y.PutInCents, y.Lines,
                drill = Drill.Range(new DateOnly(int.Parse(y.Year), 1, 1), new DateOnly(int.Parse(y.Year), 12, 31), ("asset", a.AssetId)),
            }),
            byCategory = a.ByCategory.Select(c => new { c.CategoryId, c.Name, c.PutInCents, c.Lines, drill = Range(("category", c.CategoryId ?? 0)) }),
            a.Values,
        };
    }
}
