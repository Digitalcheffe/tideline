using Tideline.Api.Auth;

namespace Tideline.Api;

/// <summary>
/// The last net under every request. The plain-words errors (<c>EditException</c>, <c>AuthException</c>) are answered where they happen; this catches
/// whatever nobody expected, writes it to the log with a reference, and answers with the same problem document as everything else. The answer never
/// carries the exception's own words, which can name tables, files or paths.
/// </summary>
public sealed class ErrorMiddleware(RequestDelegate next, ILogger<ErrorMiddleware> logger)
{
    public async Task InvokeAsync(HttpContext context)
    {
        try
        {
            await next(context);
        }
        catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested)
        {
            // the visitor went away; nothing to answer and nothing wrong
        }
        catch (BadHttpRequestException e) when (!context.Response.HasStarted)
        {
            // a body that is not the shape the route wants (the framework would answer 400 itself, outside Development)
            await Answer(context, e.StatusCode is >= 400 and < 500 ? e.StatusCode : 400, "Check the request", "That request was not understood. Check what was sent and try again.", "bad_request");
        }
        catch (Exception e) when (!context.Response.HasStarted)
        {
            // method and path only: never the query, the headers or the body, which can hold secrets
            logger.LogError(e, "Unexpected error {Reference} on {Method} {Path}", context.TraceIdentifier, context.Request.Method, context.Request.Path.Value);
            await Answer(context, 500, "Something went wrong",
                $"Something went wrong on the server. Try again. If it keeps happening, look for reference {context.TraceIdentifier} in the app log.", "server_error");
        }
        catch (Exception e)
        {
            // the answer was already on its way, so it cannot be replaced: record it and let the connection end
            logger.LogError(e, "Unexpected error {Reference} after the response started on {Method} {Path}", context.TraceIdentifier, context.Request.Method, context.Request.Path.Value);
            throw;
        }
    }

    private static Task Answer(HttpContext context, int status, string title, string detail, string type)
    {
        if (context.Request.Path.StartsWithSegments("/api")) return Problems.Write(context, status, title, detail, type);
        context.Response.StatusCode = status;
        context.Response.ContentType = "text/plain; charset=utf-8";
        return context.Response.WriteAsync(detail);
    }
}
