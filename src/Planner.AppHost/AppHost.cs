using Microsoft.Extensions.Hosting;

// The development stack, as one command.
//
//     dotnet run --project src/Planner.AppHost
//
// brings up Postgres, waits for it, migrates and seeds the API against it, and opens a dashboard with
// the logs, traces and endpoints of both. docker-compose.yml still describes the deployed system; this
// describes the one you develop against, and the two deliberately do not share a database volume.
var builder = DistributedApplication.CreateBuilder(args);

// The web client's port. Matches server.port in client/vite.config.ts, for runs without the app host.
const int ClientPort = 5175;

// A fixed password so the volume below survives a restart of the app host. This is a local development
// database that listens on a container port and holds seeded demo data; the deployed system takes its
// password from .env instead.
var databasePassword = builder.AddParameter("postgres-password", "planner-local-pw", secret: true);

var postgres = builder.AddPostgres("postgres", password: databasePassword)
    // Named, so the data outlives `Ctrl+C`. Delete the volume to start from a clean seed.
    .WithDataVolume("planner-aspire-pgdata")
    .WithPgWeb();

var database = postgres.AddDatabase("planner");

// The address a browser or an MCP client reaches Planner at. In development that is the Vite server
// below, which proxies the API, so it doubles as the token issuer and the base of the MCP resource
// (<public-url>mcp). Override it to test from outside this machine, e.g. through a tunnel:
//
//     dotnet user-secrets --project src/Planner.AppHost set Parameters:public-url https://<tunnel>/
var publicUrl = builder.AddParameter("public-url", $"http://localhost:{ClientPort}/");

var api = builder.AddProject<Projects.Planner_Api>("api")
    // The API asks for a connection string named "Planner"; the resource is named "planner". Spelling
    // the mapping out here is what keeps the two independent.
    .WithEnvironment("ConnectionStrings__Planner", database)
    // Tokens have to name one issuer however they were fetched — through the client's proxy, or
    // straight from the API in Scalar — and MCP clients must be told one resource to ask for.
    .WithEnvironment("Planner__Auth__Issuer", publicUrl)
    .WithEnvironment("Planner__Auth__McpResource", ReferenceExpression.Create($"{publicUrl}mcp"))
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
//
// Pinned to one port and not proxied: its origin is the issuer above, a passkey's relying party, and
// the address registered with MCP clients, so it cannot move from run to run. Left to Aspire, Vite
// gets a random port behind a proxy with another.
var client = builder.AddViteApp("client", ClientDirectory(builder))
    .WithEndpoint("http", endpoint =>
    {
        endpoint.Port = ClientPort;
        endpoint.TargetPort = ClientPort;
        endpoint.IsProxied = false;
    })
    .WithEnvironment("PLANNER_SERVER_URL", api.GetEndpoint("http"))
    .WithEnvironment("PLANNER_PUBLIC_URL", publicUrl)
    .WaitFor(api);

// The MCP Inspector, for trying the /mcp tools by hand. It connects through the client's origin, not to
// the API directly: that origin is the MCP resource its token is issued for, and MCP clients refuse a
// server whose metadata names a different one. Its default ports are the redirect URIs registered for
// planner-mcp in appsettings.Development.json; sign in with "Open Auth Settings" > "Quick OAuth Flow".
builder.AddMcpInspector("mcp-inspector")
    .WithMcpServer(client, isDefault: true, transportType: McpTransportType.StreamableHttp, path: "/mcp")
    .WaitFor(client);

builder.Build().Run();

// The SvelteKit client, which lives outside src/ because it is not a .NET project.
static string ClientDirectory(IDistributedApplicationBuilder builder) =>
    Path.GetFullPath(Path.Combine(builder.AppHostDirectory, "..", "..", "client"));
