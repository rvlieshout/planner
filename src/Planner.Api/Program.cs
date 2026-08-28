using System.Text.Json.Serialization;
using Microsoft.AspNetCore.DataProtection;
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

var updateFeedOptions = builder.Configuration.GetSection(UpdateFeedOptions.SectionName).Get<UpdateFeedOptions>()
                        ?? new UpdateFeedOptions();

var connectionString = builder.Configuration.GetConnectionString("Planner")
                       ?? throw new InvalidOperationException(
                           "No connection string named 'Planner'. Set ConnectionStrings__Planner.");

builder.Services.AddPlannerPersistence(connectionString);
builder.Services.AddPlannerAuth(authOptions);

builder.Services.AddHttpContextAccessor();
builder.Services.AddScoped<CurrentUser>();
builder.Services.AddScoped<ITeamAccess, TeamAccess>();
builder.Services.AddScoped<IActivityLog, ActivityLog>();
builder.Services.AddScoped<IRealtimeNotifier, RealtimeNotifier>();
builder.Services.AddScoped<DatabaseSeeder>();
builder.Services.AddScoped<OpenIddictClientSeeder>();

builder.Services
    .AddSignalR(options => options.EnableDetailedErrors = builder.Environment.IsDevelopment())
    // SignalR carries its own serializer options, so the enum-as-name choice below has to be repeated
    // here. Without this the same enum arrives as "Urgent" over REST and as 1 over the socket.
    .AddJsonProtocol(options => options.PayloadSerializerOptions.Converters.Add(new JsonStringEnumConverter()));

// Keep data-protection keys on the same mounted volume as the token certificates. Left at its default
// they land in the container filesystem and are lost on every redeploy.
builder.Services.AddDataProtection()
    .PersistKeysToFileSystem(new DirectoryInfo(Path.Combine(authOptions.KeyDirectory, "dataprotection")))
    .SetApplicationName("Planner");

// Enums travel as their names. A desktop client, curl session or log line reading "Urgent" beats one
// reading "1", and adding a new member no longer shifts the meaning of existing numbers.
builder.Services.ConfigureHttpJsonOptions(options =>
    options.SerializerOptions.Converters.Add(new JsonStringEnumConverter()));

builder.Services.AddProblemDetails();
builder.Services.AddExceptionHandler<PlannerExceptionHandler>();

builder.Services.AddHealthChecks()
    .AddDbContextCheck<PlannerDbContext>("database");

builder.Services.AddOpenApi(options => options.AddDocumentTransformer<OpenApiSecurityTransformer>());

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

app.UseExceptionHandler();

// Before authentication, and intentionally so: the desktop client checks for updates whether or not
// anyone has signed in, because a release that broke sign-in still has to be replaceable.
app.MapUpdateFeed(updateFeedOptions);

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
