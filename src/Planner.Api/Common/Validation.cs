using Microsoft.AspNetCore.Http.HttpResults;

namespace Planner.Api.Common;

/// <summary>How long free text may be. The database columns are unbounded, so without these the only
/// ceiling is the server's request size limit, and one account could store tens of megabytes per call.
/// They are set well above anything a person writes: they stop abuse, not long documents.</summary>
public static class TextLimits
{
    public const int Comment = 50_000;

    /// <summary>An issue's or a project's description.</summary>
    public const int Description = 100_000;

    public const int Document = 1_000_000;
}

/// <summary>Small accumulating validator. Endpoints collect every problem with a request before
/// answering, so a client never has to fix one field, retry, and discover the next.</summary>
public sealed class Validation
{
    private readonly Dictionary<string, List<string>> _errors = [];

    public bool HasErrors => _errors.Count > 0;

    public Validation Add(string field, string message)
    {
        if (!_errors.TryGetValue(field, out var messages))
        {
            messages = [];
            _errors[field] = messages;
        }

        messages.Add(message);
        return this;
    }

    public Validation Required(string? value, string field)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            Add(field, $"{field} is required.");
        }

        return this;
    }

    public Validation MaxLength(string? value, int max, string field)
    {
        if (value is not null && value.Length > max)
        {
            Add(field, $"{field} must be {max} characters or fewer.");
        }

        return this;
    }

    public Validation Matches(string? value, string pattern, string field, string message)
    {
        if (!string.IsNullOrEmpty(value) &&
            !System.Text.RegularExpressions.Regex.IsMatch(value, pattern))
        {
            Add(field, message);
        }

        return this;
    }

    /// <summary>A <see cref="Domain.Common.Rank"/> key sent by a client. A malformed one would sort
    /// somewhere, but no key could ever be made next to it.</summary>
    public Validation RankKey(string? value, string field)
    {
        if (value is not null && (value.Length > Domain.Common.Rank.MaxLength || !Domain.Common.Rank.IsValid(value)))
        {
            Add(field, $"{field} must be a rank key of at most {Domain.Common.Rank.MaxLength} characters, " +
                       "such as one taken from another row of the same list.");
        }

        return this;
    }

    public Validation Range(int? value, int min, int max, string field)
    {
        if (value is { } v && (v < min || v > max))
        {
            Add(field, $"{field} must be between {min} and {max}.");
        }

        return this;
    }

    public ValidationProblem ToResult() =>
        TypedResults.ValidationProblem(_errors.ToDictionary(e => e.Key, e => e.Value.ToArray()));
}
