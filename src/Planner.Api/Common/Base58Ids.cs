using System.Collections;
using System.Collections.Concurrent;
using System.Globalization;
using System.Reflection;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Http.Metadata;
using Microsoft.AspNetCore.Routing.Patterns;
using Microsoft.Extensions.Primitives;
using Planner.Contracts.Common;

namespace Planner.Api.Common;

/// <summary>Makes base58 the identifier format of the HTTP surface.
///
/// Responses are covered by a pair of JSON converters. Requests are not: a route or query value is
/// bound by the generated request delegate calling <c>Guid.TryParse</c>, and there is no hook to
/// replace that for a framework type. So the ids arriving in the URL are translated into the
/// canonical form before binding runs, and everything downstream — endpoints, EF Core, the audit
/// trail — goes on seeing nothing but <see cref="Guid"/>.</summary>
public static class Base58Ids
{
    /// <summary>Route constraint name. <c>{id:b58}</c> replaces <c>{id:guid}</c>, which would reject a
    /// base58 id before the rewrite below ever sees it.</summary>
    public const string ConstraintName = "b58";

    public static IServiceCollection AddBase58Ids(this IServiceCollection services)
    {
        services.Configure<RouteOptions>(options =>
            options.ConstraintMap[ConstraintName] = typeof(Base58IdRouteConstraint));

        return services;
    }

    /// <summary>The converters that put base58 on the wire. Shared by the REST serializer options and
    /// the SignalR protocol, which carries a serializer of its own.</summary>
    public static void AddIdConverters(this IList<JsonConverter> converters)
    {
        converters.Add(new Base58GuidConverter());
        converters.Add(new NullableBase58GuidConverter());
    }

    /// <summary>Rewrites base58 route and query values into the canonical form the parameter binder
    /// understands. Runs after routing — which <c>WebApplication</c> puts at the head of the pipeline
    /// — so the matched endpoint is already known.</summary>
    public static IApplicationBuilder UseBase58Ids(this IApplicationBuilder app) =>
        app.UseMiddleware<Base58IdBindingMiddleware>();
}

/// <summary>Matches an id in either form. Being a constraint rather than a bare <c>{id}</c> keeps the
/// old behaviour where a malformed id is a 404 from the router instead of reaching a handler, and
/// keeps two routes that differ only in their parameter's type apart.</summary>
public sealed class Base58IdRouteConstraint : IRouteConstraint
{
    public bool Match(
        HttpContext? httpContext,
        IRouter? route,
        string routeKey,
        RouteValueDictionary values,
        RouteDirection routeDirection)
    {
        if (!values.TryGetValue(routeKey, out var value) || value is null)
        {
            return false;
        }

        // Generating a link passes the Guid itself, which stringifies to the canonical form.
        var text = value as string ?? Convert.ToString(value, CultureInfo.InvariantCulture);

        return Base58.TryParseId(text, out _);
    }
}

public sealed class Base58IdBindingMiddleware(RequestDelegate next)
{
    /// <summary>Endpoints are built once and live for the lifetime of the app, so the reflection below
    /// happens on the first request to each route and never again. Held on the middleware instance —
    /// itself a singleton — rather than statically, so it does not outlive the app it describes.</summary>
    private readonly ConcurrentDictionary<Endpoint, IdParameters> _cache = new();

    public Task InvokeAsync(HttpContext context)
    {
        if (context.GetEndpoint() is { } endpoint)
        {
            var ids = _cache.GetOrAdd(endpoint, Discover);

            if (ids.RouteKeys.Length > 0)
            {
                RewriteRouteValues(context, ids.RouteKeys);
            }

            if (ids.QueryKeys.Length > 0)
            {
                RewriteQuery(context, ids.QueryKeys);
            }
        }

        return next(context);
    }

    private static void RewriteRouteValues(HttpContext context, string[] keys)
    {
        foreach (var key in keys)
        {
            if (context.Request.RouteValues.TryGetValue(key, out var value) &&
                value is string text &&
                Base58.TryParse(text, out var id))
            {
                context.Request.RouteValues[key] = id.ToString();
            }
        }
    }

    private static void RewriteQuery(HttpContext context, string[] keys)
    {
        Dictionary<string, StringValues>? rewritten = null;

        foreach (var key in keys)
        {
            if (!context.Request.Query.TryGetValue(key, out var values))
            {
                continue;
            }

            string?[]? replacement = null;

            // Repeated parameters — ?assigneeId=..&assigneeId=.. — are an OR set, and each entry is
            // decoded on its own.
            for (var i = 0; i < values.Count; i++)
            {
                if (!Base58.TryParse(values[i], out var id))
                {
                    continue;
                }

                replacement ??= values.ToArray();
                replacement[i] = id.ToString();
            }

            if (replacement is null)
            {
                continue;
            }

            rewritten ??= context.Request.Query.ToDictionary(
                pair => pair.Key,
                pair => pair.Value,
                StringComparer.OrdinalIgnoreCase);

            rewritten[key] = new StringValues(replacement);
        }

        if (rewritten is not null)
        {
            context.Request.Query = new QueryCollection(rewritten);
        }
    }

    /// <summary>Finds the names a handler binds as identifiers, so nothing else in the URL is touched:
    /// <c>?search=</c> and <c>?sort=</c> are free to hold 22 characters of base58 and mean it
    /// literally.</summary>
    private static IdParameters Discover(Endpoint endpoint)
    {
        if (endpoint.Metadata.GetMetadata<MethodInfo>() is not { } handler)
        {
            return IdParameters.None;
        }

        var routeNames = RouteParameterNames(endpoint);
        List<string> routeKeys = [];
        List<string> queryKeys = [];

        foreach (var parameter in handler.GetParameters())
        {
            if (parameter.GetCustomAttribute<AsParametersAttribute>() is null)
            {
                Collect(parameter.ParameterType, parameter.Name, parameter, routeNames, routeKeys, queryKeys);
                continue;
            }

            // [AsParameters] flattens a record's properties across the route and the query string.
            foreach (var property in parameter.ParameterType.GetProperties(BindingFlags.Public | BindingFlags.Instance))
            {
                Collect(property.PropertyType, property.Name, property, routeNames, routeKeys, queryKeys);
            }
        }

        return routeKeys.Count == 0 && queryKeys.Count == 0
            ? IdParameters.None
            : new IdParameters([.. routeKeys], [.. queryKeys]);
    }

    private static void Collect(
        Type type,
        string? name,
        ICustomAttributeProvider source,
        HashSet<string> routeNames,
        List<string> routeKeys,
        List<string> queryKeys)
    {
        if (name is null || !IsIdentifier(type))
        {
            return;
        }

        // A header or a body never carries a route value to rewrite, and a Guid-shaped service is not
        // bound from the request at all.
        var attributes = source.GetCustomAttributes(inherit: true);
        if (attributes.Any(a => a is IFromHeaderMetadata or IFromBodyMetadata or IFromServiceMetadata))
        {
            return;
        }

        var bound = attributes.OfType<IFromRouteMetadata>().FirstOrDefault()?.Name
                    ?? attributes.OfType<IFromQueryMetadata>().FirstOrDefault()?.Name
                    ?? name;

        if (routeNames.Contains(bound))
        {
            routeKeys.Add(bound);
        }
        else
        {
            queryKeys.Add(bound);
        }
    }

    private static HashSet<string> RouteParameterNames(Endpoint endpoint)
    {
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        if (endpoint is RouteEndpoint { RoutePattern: { } pattern })
        {
            foreach (RoutePatternParameterPart part in pattern.Parameters)
            {
                names.Add(part.Name);
            }
        }

        return names;
    }

    /// <summary>A Guid, a Guid?, or a collection of either — which is how the OR-set filters arrive.</summary>
    private static bool IsIdentifier(Type type)
    {
        var target = Nullable.GetUnderlyingType(type) ?? type;

        if (target == typeof(Guid))
        {
            return true;
        }

        if (target.IsArray)
        {
            return IsIdentifier(target.GetElementType()!);
        }

        return target.IsGenericType &&
               target.GetGenericArguments() is [var element] &&
               typeof(IEnumerable).IsAssignableFrom(target) &&
               IsIdentifier(element);
    }

    private sealed record IdParameters(string[] RouteKeys, string[] QueryKeys)
    {
        public static readonly IdParameters None = new([], []);
    }
}
