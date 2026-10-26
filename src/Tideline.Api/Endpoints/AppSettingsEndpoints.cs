using Tideline.Data;

namespace Tideline.Api.Endpoints;

/// <summary>
/// The few options that start from the compose file and can be changed in the app (sign-in timing and limits, the inbox folder). Owner only
/// (the /settings prefix). See <see cref="RuntimeSettings"/>.
/// </summary>
public static class AppSettingsEndpoints
{
    public static void MapAppSettingsEndpoints(this RouteGroupBuilder api)
    {
        api.MapGet("/settings/app", (RuntimeSettings settings, SqliteConnectionFactory f) =>
        {
            using var conn = f.Open();
            return Results.Ok(settings.Describe(conn));
        });

        // values: set these (an empty inbox folder turns the inbox off); reset: go back to what compose says
        api.MapPut("/settings/app", (AppSettingsRequest request, RuntimeSettings settings, HttpContext http, SqliteConnectionFactory f, TimeProvider t) =>
            Edits.Run(http, f, t, (c, clock, who) =>
            {
                new AppSettingsEditor(c, clock, who, settings).Save(request.Values ?? [], request.Reset ?? []);
                return settings.Describe(c);
            }));
    }

    public sealed record AppSettingsRequest(Dictionary<string, string?>? Values, string[]? Reset);
}
