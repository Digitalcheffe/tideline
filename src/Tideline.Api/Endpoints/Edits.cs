using System.Data;
using Tideline.Api.Auth;
using Tideline.Data;
using Tideline.Data.Services;

namespace Tideline.Api.Endpoints;

/// <summary>Runs one change on its own connection and turns the plain-words errors into problem documents.</summary>
public static class Edits
{
    public static IResult Run(HttpContext http, SqliteConnectionFactory factory, TimeProvider time, Func<IDbConnection, TimeProvider, Actor, object> action, bool created = false)
    {
        try
        {
            using var conn = factory.Open();
            var result = action(conn, time, http.Actor());
            return created ? Results.Json(result, statusCode: StatusCodes.Status201Created) : Results.Ok(result);
        }
        catch (EditException e)
        {
            var (status, title) = e.Kind switch
            {
                EditError.NotFound => (404, "Not found"),
                EditError.Conflict => (409, "Conflict"),
                EditError.Unprocessable => (422, "Cannot do that"),
                _ => (400, "Check the request"),
            };
            return Results.Problem(title: title, detail: e.Message, statusCode: status);
        }
    }
}
