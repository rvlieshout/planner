using System.Text.Json.Serialization;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.HttpOverrides;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.RateLimiting;
using Planner.Api.Auth;
using Planner.Api.Authorization;
using Planner.Api.Common;
using Planner.Api.Endpoints;
using Planner.Api.Realtime;
using Planner.Api.Startup;
using Planner.Infrastructure;
using Planner.Infrastructure.Seeding;
using Scalar.AspNetCore;

var builder = WebApplication.CreateBuilder(args);

var authOptions = builder.Configuration.GetSection(PlannerAuthOptions.SectionName).Get<PlannerAuthOptions>()
                  ?? new PlannerAuthOptions();

builder.Services.Configure<PlannerAuthOptions>(builder.Configuration.GetSection(PlannerAuthOptions.SectionName));
builder.Services.Configure<PlannerSeedOptions>(builder.Configuration.GetSection(PlannerSeedOptions.SectionName));
builder.Services.Configure<DatabaseOptions>(builder.Configuration.GetSection(DatabaseOptions.SectionName));

var connectionString = builder.Configuration.GetConnectionString("Planner")
                       ?? throw new InvalidOperationException(
                           "No connection string named 'Planner'. Set ConnectionStrings__Planner.");

builder.Services.AddPlannerPersistence(connectionString);
builder.Services.AddPlannerAuth(authOptions);

// Ids are uuids in the database and base58 on the wire. This registers the {id:b58} route constraint;
// the converters below and the middleware further down are the other two halves.
builder.Services.AddBase58Ids();

builder.Services.AddHttpContextAccessor();
builder.Services.AddScoped<CurrentUser>();
builder.Services.AddScoped<ITeamAccess, TeamAccess>();
builder.Services.AddScoped<IActivityLog, ActivityLog>();
builder.Services.AddScoped<IRealtimeNotifier, RealtimeNotifier>();
builder.Services.AddSingleton<RealtimeConnections>();
builder.Services.AddScoped<IRealtimeSubscriptions, RealtimeSubscriptions>();
builder.Services.AddScoped<DatabaseSeeder>();
builder.Services.AddScoped<OpenIddictClientSeeder>();

builder.Services
    .AddSignalR(options => options.EnableDetailedErrors = builder.Environment.IsDevelopment())
    // SignalR carries its own serializer options, so the enum-as-name and base58-id choices below have
    // to be repeated here. Without this the same enum arrives as "Urgent" over REST and as 1 over the
    // socket, and an issue id that the client just read from a REST response would not match the one
    // in the change pushed to it.
    .AddJsonProtocol(options =>
    {
        options.PayloadSerializerOptions.Converters.Add(new JsonStringEnumConverter());
        options.PayloadSerializerOptions.Converters.AddIdConverters();
    });

// Keep data-protection keys on the same mounted volume as the token certificates. Left at its default
// they land in the container filesystem and are lost on every redeploy.
builder.Services.AddDataProtection()
    .PersistKeysToFileSystem(new DirectoryInfo(Path.Combine(authOptions.KeyDirectory, "dataprotection")))
    .SetApplicationName("Planner");

// Enums travel as their names. A desktop client, curl session or log line reading "Urgent" beats one
// reading "1", and adding a new member no longer shifts the meaning of existing numbers.
builder.Services.ConfigureHttpJsonOptions(options =>
{
    options.SerializerOptions.Converters.Add(new JsonStringEnumConverter());

    // Ids leave as base58 and arrive as either form. The database column stays a uuid; this is the
    // only place the two representations meet on the response side.
    options.SerializerOptions.Converters.AddIdConverters();
});

builder.Services.AddProblemDetails();
builder.Services.AddExceptionHandler<PlannerExceptionHandler>();

builder.Services.AddHealthChecks()
    .AddDbContextCheck<PlannerDbContext>("database");

builder.Services.AddOpenApi(options =>
{
    options.AddDocumentTransformer<OpenApiSecurityTransformer>();
    options.AddSchemaTransformer<Base58IdSchemaTransformer>();
});

builder.Services.AddCors(options => options.AddDefaultPolicy(policy =>
{
    if (authOptions.AllowedOrigins.Length == 0)
    {
        return;
    }

    policy.WithOrigins(authOptions.AllowedOrigins)
        .AllowAnyHeader()
        .AllowAnyMethod()
        .AllowCredentials();
}));

if (authOptions.TrustedProxyHops > 0)
{
    builder.Services.Configure<ForwardedHeadersOptions>(options =>
    {
        options.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
        options.ForwardLimit = authOptions.TrustedProxyHops;

        // Out of the box only loopback is trusted, which is never where a container's proxy lives.
        // Trusting the private ranges is safe here because the API publishes no host port: nothing
        // outside the container networks can reach it to forge a header in the first place.
        options.KnownIPNetworks.Clear();
        options.KnownProxies.Clear();
        // Qualified, because Microsoft.AspNetCore.HttpOverrides has an IPNetwork of its own and it is
        // the deprecated one.
        options.KnownIPNetworks.Add(System.Net.IPNetwork.Parse("10.0.0.0/8"));
        options.KnownIPNetworks.Add(System.Net.IPNetwork.Parse("172.16.0.0/12"));
        options.KnownIPNetworks.Add(System.Net.IPNetwork.Parse("192.168.0.0/16"));
    });
}

builder.Services.AddRateLimiter(options =>
{
    options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;

    // Password grants are the one endpoint worth brute-forcing, so it gets its own budget per client IP.
    options.AddPolicy("auth", context => RateLimitPartition.GetFixedWindowLimiter(
        context.Connection.RemoteIpAddress?.ToString() ?? "unknown",
        _ => new FixedWindowRateLimiterOptions
        {
            PermitLimit = 20,
            Window = TimeSpan.FromMinutes(1),
            QueueLimit = 0
        }));
});

var app = builder.Build();

await app.InitializeDatabaseAsync();

// First in the pipeline, so everything after it — logging, rate limiting, the URIs OpenIddict builds
// for itself — sees the caller and scheme of the public request rather than the proxy's.
if (authOptions.TrustedProxyHops > 0)
{
    app.UseForwardedHeaders();
}

app.UseExceptionHandler();

// After the implicit UseRouting at the head of the pipeline, so the matched endpoint is known, and
// before anything binds parameters: base58 ids in the path and query are canonicalised here.
app.UseBase58Ids();

app.UseMiddleware<SignalRAuthenticationMiddleware>("/hubs");
app.UseCors();
app.UseRateLimiter();
app.UseAuthentication();
app.UseAuthorization();

// The schema and its UI are readable without a token: the fallback policy would otherwise lock the
// Scalar page out of the very document it needs to render. Every endpoint it describes still requires one.
app.MapOpenApi().AllowAnonymous();
app.MapScalarApiReference(options => options
        .WithTitle("Planner API")
        .WithTheme(ScalarTheme.Purple))
    .AllowAnonymous();

app.MapAuthEndpoints();
app.MapUserEndpoints();
app.MapTeamEndpoints();
app.MapProjectEndpoints();
app.MapDocumentEndpoints();
app.MapIssueEndpoints();

app.MapHub<PlannerHub>("/hubs/planner");

app.MapHealthChecks("/health/live", new Microsoft.AspNetCore.Diagnostics.HealthChecks.HealthCheckOptions
{
    Predicate = _ => false
}).AllowAnonymous();

app.MapHealthChecks("/health/ready").AllowAnonymous();

app.Run();
