# MCP server

Planner exposes a [Model Context Protocol](https://modelcontextprotocol.io) endpoint at `/mcp`, so an
AI assistant can answer questions like *"what happened in the development team last week?"* from live
data, and file issues, as the signed-in user.

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

Tools see only what the user can see in the app, and can change only what the user could change there.
All but `create_issue` are read-only.

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
| `create_issue` | **Files an issue as the user.** Team and title are required; state, priority, assignee, project, milestone, parent, labels, estimate and due date are optional and given by name. Returns the new key and a link. |

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

- **One write: creating issues.** `create_issue` goes through `IssueCreator`, the same code as
  `POST /api/v1/issues`, so it needs `Write` on the team (guests and viewers are refused), is validated,
  numbered and ranked the same way, is recorded in the activity log as the user, and appears live in
  open browsers. It is annotated as a write, so MCP clients ask the user before calling it.
- **Retries don't duplicate.** An identical title from the same user in the same team within 10
  minutes returns the existing issue, unless the call passes `allowDuplicate`.
- Reading the inbox does not mark it read.
- **The user's permissions, never more.** Every query is scoped through `ITeamAccess`, like the REST
  endpoints. A guest's assistant sees what the guest sees.
- **MCP tokens stay on `/mcp`.** A token issued to an assistant carries the MCP resource as its
  audience. `/mcp` accepts only those tokens, and the rest of the API (REST and realtime) refuses
  them, so approving an assistant hands it the tools on this endpoint and nothing else the user can
  do: it can file issues, but not edit, move or delete anything.
- **Deactivation is immediate.** `/mcp` checks the account is still active on every call, so a
  deactivated user's assistant stops at once instead of when its access token expires.
- **Ambiguity is refused, not guessed.** Two teams can own projects with the same name; a name that
  matches more than one visible project is answered with the candidates, never with one of them.
- **Rate limited** per user: 120 calls a minute.
- **Stateless.** Every call carries its own bearer token. Nothing about a caller is kept between calls.

## Adding a tool

Reads go straight to the database through `McpReader`. Writes must go through the same service as the
REST endpoint that makes that change (as `create_issue` uses `IssueCreator`), never a second copy of
it, so permissions, validation, the activity log and realtime updates cannot drift apart.


Add a method to one of the `[McpServerToolType]` classes in `src/Planner.Api/Mcp/`, or a new class
registered in `McpSetup.AddPlannerMcp`. Start every query from `McpReader.ReadableTeamIdsAsync`, return
`McpReader.Serialize(...)`, and throw `McpException` with a message the model can act on. Add the
tool's name to the list in `tests/Planner.Auth.Checks/McpEndpointChecks.cs`.
