# MCP server

Planner exposes a read-only [Model Context Protocol](https://modelcontextprotocol.io) endpoint at
`/mcp`, so an AI assistant can answer questions like *"what happened in the development team last
week?"* from live data, as the signed-in user.

It runs inside the API (`src/Planner.Api/Mcp/`), on the same database and the same permission rules
as the REST endpoints. How an assistant gets a token is in [mcp-authorization.md](mcp-authorization.md).

## Connecting

| | Production | Development (AppHost) |
| --- | --- | --- |
| Server URL | `https://<your install>/mcp` | `http://localhost:5175/mcp` |
| Transport | Streamable HTTP, stateless | same |
| Sign-in | OAuth, discovered automatically | same |

A client pointed at the URL gets a `401` whose `WWW-Authenticate` header links to
`/.well-known/oauth-protected-resource/mcp`. That names Planner as the authorization server, and the
client signs in from there.

### Trying it with the MCP Inspector

The AppHost starts the [MCP Inspector](https://github.com/modelcontextprotocol/inspector) as
`mcp-inspector`, already pointed at `http://localhost:5175/mcp`. Open it from the Aspire dashboard (the
link carries the proxy token), then:

1. **Open Auth Settings → Quick OAuth Flow.** The Inspector discovers Planner, registers itself, and
   your browser lands on Planner's consent page (where it shows as unverified, since it named itself).
   Sign in there if needed, and **Allow**.
2. **Connect**, then **Tools → List Tools**, and call `team_digest` with `{"team": "DEV"}`.

It connects through the web client's origin rather than to the API directly, because that origin is
the resource its token is issued for. Its ports (6274 for the UI, 6277 for the proxy) match the
`McpRedirectUris` and `AllowedOrigins` in `appsettings.Development.json`, so leave them as they are.

## Tools

All tools are read-only, and they only see what the user can see in the app.

| Tool | For |
| --- | --- |
| `team_digest` | **What happened in a team over a period.** Issues completed, created, started and canceled in the window; what is in progress now and what went quiet; overdue work; project and milestone progress; who was active. One call answers the weekly-summary question. |
| `list_teams` | Teams the user can see, with keys and the user's role. |
| `list_projects` | Projects with status, health, lead, dates and issue progress. |
| `get_project` | One project: brief, milestones with progress, issues in progress. |
| `search_issues` | Issues by team, project, state type, assignee (`me` works), priority, text, or recent change. |
| `get_issue` | One issue by key (`DEV-42`): description, sub-issues, relations, latest comments, history. |
| `get_inbox` | The user's inbox. Reading it marks nothing as read. |
| `get_activity` | The raw change feed, filterable by team, project, person and period. |

Tools take teams by key, name or a unique part of a name; projects by name; people by `me`, email or
display name. An unknown team is answered with the list of known ones, so the model can correct itself.

The `team_summary` prompt packages the weekly-summary question: it tells the model to call
`team_digest` once and write *Highlights, Done, New, In progress, Projects*.

### Output

Results are compact JSON meant for a model, not the REST DTOs: issues by key, people by name, enums
by name, no colours or ranks, nothing null. Timestamps are in the user's time zone (from their
profile), so "Monday" in the answer is the user's Monday.

On a Windows development machine they come out in UTC: the repo builds with
`InvariantGlobalization`, and without ICU Windows cannot resolve IANA zone ids such as
`Europe/Amsterdam`. The Linux images read `/usr/share/zoneinfo` directly and are not affected.

## Boundaries

- **Read-only.** No tool writes. Reading the inbox does not mark it read.
- **The user's permissions, never more.** Every query is scoped through `ITeamAccess`, like the REST
  endpoints. A guest's assistant sees what the guest sees.
- **MCP tokens stay on `/mcp`.** A token issued to an assistant carries the MCP resource as its
  audience. `/mcp` accepts only those tokens, and the rest of the API (REST and realtime) refuses
  them, so approving a read-only assistant never hands it the user's write access.
- **Deactivation is immediate.** `/mcp` checks the account is still active on every call, so a
  deactivated user's assistant stops at once instead of when its access token expires.
- **Ambiguity is refused, not guessed.** Two teams can own projects with the same name; a name that
  matches more than one visible project is answered with the candidates, never with one of them.
- **Rate limited** per user: 120 calls a minute.
- **Stateless.** Every call carries its own bearer token. Nothing about a caller is kept between calls.

## Adding a tool

Add a method to one of the `[McpServerToolType]` classes in `src/Planner.Api/Mcp/`, or a new class
registered in `McpSetup.AddPlannerMcp`. Start every query from `McpReader.ReadableTeamIdsAsync`, return
`McpReader.Serialize(...)`, and throw `McpException` with a message the model can act on. Add the
tool's name to the list in `tests/Planner.Auth.Checks/McpEndpointChecks.cs`.
