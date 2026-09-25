using Microsoft.AspNetCore.Http.HttpResults;

namespace Planner.Api.Common;

/// <summary>The outcome of a write shared by the REST endpoints and the MCP tools: the thing as it now
/// is, or the refusal. The refusal is the endpoint's own HTTP answer, so REST responses stay exactly as
/// they were, and <see cref="ErrorMessage"/> reads it back as a sentence for callers that are not
/// answering an HTTP request.</summary>
public sealed record WriteResult<T>(T? Value, IResult? Error) where T : class
{
    public static WriteResult<T> Succeeded(T value) => new(value, null);

    public static WriteResult<T> Failed(IResult error) => new(null, error);

    public bool IsSuccess => Error is null;

    public string? ErrorMessage => Error switch
    {
        null => null,
        ProblemHttpResult problem => problem.ProblemDetails.Detail ?? problem.ProblemDetails.Title,
        ValidationProblem validation => string.Join(" ", validation.ProblemDetails.Errors.SelectMany(e => e.Value)),
        _ => "The change could not be made."
    };

    /// <summary>The answer to an HTTP request: the refusal, or 200 with the value.</summary>
    public IResult ToResult() => Error ?? Results.Ok(Value);

    /// <summary>The refusal, or whatever success looks like for this endpoint (201, 204, ...).</summary>
    public IResult ToResult(Func<T, IResult> success) => Error ?? success(Value!);
}
