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
    .WaitFor(database)

    // Everything else the API needs for local work already lives in appsettings.Development.json —
    // the dev signing keys, the seeded owner, the demo data — and Aspire runs projects in the
    // Development environment, so it is not repeated here. The one exception is the update feed, whose
    // default is a Linux container path:
    .WithEnvironment("Planner__Updates__Directory", ReleasesDirectory(builder));

// The desktop client, on explicit start.
//
// It is a window rather than a service: auto-launching it would put a window on screen every time
// someone starts the API. The dashboard's Start button runs it against whatever port the API landed
// on, which is the part that is otherwise fiddly to get right by hand.
builder.AddProject<Projects.Planner_Client>("client")
    .WithEnvironment("PLANNER_SERVER_URL", api.GetEndpoint("http"))
    .WaitFor(api)
    .WithExplicitStart();

builder.Build().Run();

// The folder build/release.ps1 publishes into, which is what the API serves at /updates. Pointing the
// API at it means a locally built release is installable by a locally running client, exactly as it
// would be in production.
static string ReleasesDirectory(IDistributedApplicationBuilder builder) =>
    Path.GetFullPath(Path.Combine(builder.AppHostDirectory, "..", "..", "releases"));
