using Microsoft.AspNetCore.Diagnostics;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace Planner.Api.Common;

/// <summary>Turns the database's own guarantees into HTTP answers. A unique index or a stale
/// concurrency token is a 409, not a 500 — the client can act on the first and should retry the second.</summary>
public sealed class PlannerExceptionHandler(ILogger<PlannerExceptionHandler> logger) : IExceptionHandler
{
    public async ValueTask<bool> TryHandleAsync(
        HttpContext context,
        Exception exception,
        CancellationToken cancellationToken)
    {
        var (status, title, detail) = exception switch
        {
            DbUpdateConcurrencyException => (
                StatusCodes.Status409Conflict,
                "Conflict",
                "Someone else changed this record while you were editing it. Reload and try again."),

            DbUpdateException { InnerException: PostgresException { SqlState: "23505" } } => (
                StatusCodes.Status409Conflict,
                "Conflict",
                "That value already exists."),

            DbUpdateException { InnerException: PostgresException { SqlState: "23503" } } => (
                StatusCodes.Status400BadRequest,
                "Bad request",
                "The request references something that does not exist."),

            _ => (0, string.Empty, string.Empty)
        };

        if (status == 0)
        {
            return false;
        }

        logger.LogWarning(exception, "Request failed with {Status}: {Title}", status, title);

        context.Response.StatusCode = status;
        await context.Response.WriteAsJsonAsync(
            new
            {
                type = $"https://httpstatuses.io/{status}",
                title,
                status,
                detail,
                instance = context.Request.Path.Value
            },
            cancellationToken);

        return true;
    }
}
