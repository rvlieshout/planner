using System.Text.Json;
using OpenIddict.Abstractions;

namespace Planner.Api.Auth;

/// <summary>Removes MCP clients that registered themselves and were never used.
///
/// Registration is open to anyone who can reach the server, and most registrations are a client that
/// was pointed here once: every one is a row, and nothing else ever removes them. A client is removed
/// when it registered more than <see cref="Unused"/> ago and has never been issued so much as an
/// authorization code. One that anybody has approved keeps its tokens on record and is left alone, as
/// is every client that was configured rather than registered.</summary>
public sealed class McpClientCleanup(
    IOpenIddictApplicationManager applications,
    IOpenIddictTokenManager tokens,
    ILogger<McpClientCleanup> logger)
{
    /// <summary>Long enough for someone who added a connector and came back to finish signing in weeks
    /// later: the client they added still has the id it was given.</summary>
    public static readonly TimeSpan Unused = TimeSpan.FromDays(30);

    public async Task<int> SweepAsync(DateTimeOffset now, CancellationToken ct = default)
    {
        // Read to the end first: the checks below query on the same connection.
        var registered = new List<object>();
        await foreach (var application in applications.ListAsync(cancellationToken: ct))
        {
            registered.Add(application);
        }

        var removed = 0;

        foreach (var application in registered)
        {
            var properties = await applications.GetPropertiesAsync(application, ct);

            if (!properties.TryGetValue(McpClients.DynamicProperty, out var dynamic) || dynamic.ValueKind != JsonValueKind.True)
            {
                continue;
            }

            // Registered before registrations were dated. Its time starts now, so it gets the same
            // grace as a new one instead of being removed on the day of the upgrade.
            if (!properties.TryGetValue(McpClients.RegisteredProperty, out var stamp) ||
                !stamp.TryGetInt64(out var seconds))
            {
                var descriptor = new OpenIddictApplicationDescriptor();
                await applications.PopulateAsync(descriptor, application, ct);
                descriptor.Properties[McpClients.RegisteredProperty] = JsonSerializer.SerializeToElement(now.ToUnixTimeSeconds());
                await applications.UpdateAsync(application, descriptor, ct);
                continue;
            }

            if (now - DateTimeOffset.FromUnixTimeSeconds(seconds) < Unused)
            {
                continue;
            }

            var id = await applications.GetIdAsync(application, ct);
            if (id is null || await AnyAsync(tokens.FindByApplicationIdAsync(id, ct), ct))
            {
                continue;
            }

            await applications.DeleteAsync(application, ct);
            removed++;
        }

        if (removed > 0)
        {
            logger.LogInformation("Removed {Count} self-registered MCP clients that were never used", removed);
        }

        return removed;
    }

    private static async Task<bool> AnyAsync(IAsyncEnumerable<object> items, CancellationToken ct)
    {
        await using var enumerator = items.GetAsyncEnumerator(ct);
        return await enumerator.MoveNextAsync();
    }
}

/// <summary>Runs <see cref="McpClientCleanup"/> shortly after start and then once a day.</summary>
public sealed class McpClientSweeper(IServiceScopeFactory scopes, ILogger<McpClientSweeper> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await Task.Delay(TimeSpan.FromMinutes(1), stoppingToken);

            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    await using var scope = scopes.CreateAsyncScope();
                    await scope.ServiceProvider.GetRequiredService<McpClientCleanup>()
                        .SweepAsync(DateTimeOffset.UtcNow, stoppingToken);
                }
                catch (Exception error) when (error is not OperationCanceledException)
                {
                    // Housekeeping: a failed pass costs nothing but rows, and tomorrow's will catch up.
                    logger.LogWarning(error, "Removing unused MCP clients failed");
                }

                await Task.Delay(TimeSpan.FromDays(1), stoppingToken);
            }
        }
        catch (OperationCanceledException)
        {
            // Shutting down.
        }
    }
}
