using Microsoft.AspNetCore.Http.HttpResults;

namespace Planner.Api.Common;

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
