using System.Text.Json.Serialization;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Planner.Contracts.Issues;
using Planner.Contracts.Realtime;

namespace Planner.Client.Services;

/// <summary>The SignalR half of the client. Subscribes to the hub as the signed-in user and raises
/// board changes as they happen, so the board is never stale and never polled.</summary>
public sealed class RealtimeService(AuthService auth, ILogger<RealtimeService> logger) : IAsyncDisposable
{
    private HubConnection? _connection;

    public event Action<EntityChange<IssueSummary>>? IssueChanged;

    public event Action<bool>? ConnectedChanged;

    public bool IsConnected => _connection?.State == HubConnectionState.Connected;

    public async Task ConnectAsync(string serverUrl, CancellationToken ct)
    {
        await DisconnectAsync();

        var url = $"{serverUrl.TrimEnd('/')}/hubs/planner";

        var connection = new HubConnectionBuilder()
            .WithUrl(url, options => options.AccessTokenProvider = () => Task.FromResult(auth.AccessToken))
            // Mirrors the server: enums travel as names on this socket too.
            .AddJsonProtocol(options =>
                options.PayloadSerializerOptions.Converters.Add(new JsonStringEnumConverter()))
            .WithAutomaticReconnect()
            .Build();

        connection.On<EntityChange<IssueSummary>>(
            nameof(IPlannerClient.IssueChanged),
            change => IssueChanged?.Invoke(change));

        connection.On<IReadOnlyList<string>>(
            nameof(IPlannerClient.Subscribed),
            groups => logger.LogInformation("Subscribed to {Count} realtime groups", groups.Count));

        connection.Reconnected += _ =>
        {
            logger.LogInformation("Realtime connection re-established");
            ConnectedChanged?.Invoke(true);
            return Task.CompletedTask;
        };

        connection.Reconnecting += _ =>
        {
            ConnectedChanged?.Invoke(false);
            return Task.CompletedTask;
        };

        connection.Closed += _ =>
        {
            ConnectedChanged?.Invoke(false);
            return Task.CompletedTask;
        };

        try
        {
            await connection.StartAsync(ct);
            _connection = connection;
            ConnectedChanged?.Invoke(true);
            logger.LogInformation("Realtime connected to {Url}", url);
        }
        catch (Exception ex)
        {
            // The board still works over REST; live updates are an enhancement, not a prerequisite.
            logger.LogWarning(ex, "Could not open the realtime connection to {Url}", url);
            await connection.DisposeAsync();
            ConnectedChanged?.Invoke(false);
        }
    }

    public async Task DisconnectAsync()
    {
        if (_connection is null)
        {
            return;
        }

        var connection = _connection;
        _connection = null;

        try
        {
            await connection.StopAsync();
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "Error while stopping the realtime connection");
        }
        finally
        {
            await connection.DisposeAsync();
            ConnectedChanged?.Invoke(false);
        }
    }

    public async ValueTask DisposeAsync() => await DisconnectAsync();
}
