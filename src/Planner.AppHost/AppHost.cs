using Microsoft.Extensions.Hosting;

// The development stack, as one command.
//
//     dotnet run --project src/Planner.AppHost
//
// brings up Postgres, waits for it, migrates and seeds the API against it, and opens a dashboard with
// the logs, traces and endpoints of both. docker-compose.yml still describes the deployed system; this
// describes the one you develop against, and the two deliberately do not share a database volume.
var builder = DistributedApplication.CreateBuilder(args);

// A fixed password so the volume below survives a restart of the app host. This is a local development
// database that listens on a container port and holds seeded demo data; the deployed system takes its
// password from .env instead.
var databasePassword = builder.AddParameter("postgres-password", "planner-local-pw", secret: true);

var postgres = builder.AddPostgres("postgres", password: databasePassword)
    // Named, so the data outlives `Ctrl+C`. Delete the volume to start from a clean seed.
    .WithDataVolume("planner-aspire-pgdata")
    .WithPgWeb();

var database = postgres.AddDatabase("planner");

var api = builder.AddProject<Projects.Planner_Api>("api")
    // The API asks for a connection string named "Planner"; the resource is named "planner". Spelling
    // the mapping out here is what keeps the two independent.
    .WithEnvironment("ConnectionStrings__Planner", database)
    // Everything else the API needs for local work already lives in appsettings.Development.json —
    // the dev signing keys, the seeded owner, the demo data — and Aspire runs projects in the
    // Development environment, so it is not repeated here.
    .WaitFor(database);

// The web client, on Vite's development server.
//
// PLANNER_SERVER_URL is read by client/vite.config.ts, which proxies /api, /connect and /hubs to it.
// That proxy is what makes development look like production: in both, the browser only ever talks to
// the origin it was served from, so there is no CORS to configure and no second address to keep in
// step. In production the same job is done by Caddy, in the image deploy/web.Dockerfile builds.
builder.AddViteApp("client", ClientDirectory(builder))
    .WithEnvironment("PLANNER_SERVER_URL", api.GetEndpoint("http"))
    .WaitFor(api);

builder.Build().Run();

// The SvelteKit client, which lives outside src/ because it is not a .NET project.
static string ClientDirectory(IDistributedApplicationBuilder builder) =>
    Path.GetFullPath(Path.Combine(builder.AppHostDirectory, "..", "..", "client"));
