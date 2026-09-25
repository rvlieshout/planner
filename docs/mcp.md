# MCP server

Planner exposes a [Model Context Protocol](https://modelcontextprotocol.io) endpoint at `/mcp`, so an
AI assistant can answer questions like *"what happened in the development team last week?"* from live
data, and work in Planner as the signed-in user: file, edit, move and comment on issues, and keep project briefs,
documents and issue notes up to date.

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

Tools see only what the user can see in the app, and change only what the user could change there.

### Reading

| Tool | For |
| --- | --- |
| `team_digest` | **What happened in a team over a period.** Issues completed, created, started and canceled in the window; what is in progress now and what went quiet; overdue work; project and milestone progress; who was active. One call answers the weekly-summary question. |
| `list_teams` | Teams the user can see, with keys and the user's role. |
| `list_projects` | Projects with status, health, lead, dates and issue progress. |
| `get_project` | One project: its brief (description), milestones with progress, issues in progress, its documents, and its `version`. |
| `search_issues` | Issues by team, project, state type, assignee (`me` works), priority, text, key, or recent change. |
| `get_issue` | One issue by key (`DEV-42`): description, sub-issues, relations, the latest 30 comments (with ids, and `mine` on the user's own), attachments, history, and its `version`. |
| `list_comments` | An issue's whole comment thread, a page at a time, newest page first. |
| `get_inbox` | The user's inbox. Reading it marks nothing as read. |
| `get_activity` | The raw change feed, filterable by team, project, person and period. |
| `list_documents` | Documents by team, project or text. |
| `get_document` | A document's Markdown, with its `version`. |
| `read_attachment` | The text of a file on an issue, up to 256 KB. Binary files and links are described, not read. |

### Changing

| Tool | For |
| --- | --- |
| `create_issue` | File an issue. Team and title are required; everything else is optional and given by name. |
| `update_issue` | Edit an issue's fields: title, description, priority, assignee, project, milestone, parent, labels (set, add or remove), estimate, due date. Empty a field by listing it in `clear`. |
| `move_issue` | Move an issue to another state and/or place it in its column (top, bottom, or next to another issue), as dragging it on the board would. |
| `update_project` | Edit a project's brief, summary, name, status, health, lead and dates. |
| `create_document` | Write a Markdown document in a team, optionally on a project. A second document with the same title in the same place is refused in favour of `update_document`. |
| `update_document` | Rename a document, append to it, or rewrite it. |
| `attach_text` | Attach a text file to an issue, or replace one with the same name. At most 1 MB. |
| `add_comment` | Comment on an issue in Markdown, or reply to a comment by id. |
| `update_comment` | Rewrite one of the user's own comments. Other people's comments cannot be edited, whatever the user's role. |

Tools take teams by key, name or a unique part of a name; projects, documents and labels by name;
workflow states by name or type; people by `me`, email or display name. An unknown name is answered
with the valid choices, so the model can correct itself; one that matches several is refused with the
candidates, never guessed.

The `team_summary` prompt packages the weekly-summary question: it tells the model to call
`team_digest` once and write *Highlights, Done, New, In progress, Projects*.

### A project's knowledge

The server's instructions tell assistants where a project's knowledge lives, so agents working on it
read it first and keep it current. The project's **description** is its brief: what it is, where it
stands, where it is going. Its **documents** hold the rest (overview, current state, architecture,
decisions, direction), one document per subject. Material about one issue goes in text
**attachments** on that issue.

### Editing text without losing anyone's work

Descriptions, briefs and documents can be changed two ways:

- **Append** (`appendToDescription`, `append`) adds a paragraph to the end. It needs nothing and loses
  nothing.
- **Replace** (`description`, `content`) needs the `version` returned when the text was read. If the
  issue, project or document changed since, the write is refused and the assistant is told to read
  again and reapply its edit, so a person's edit made in the meantime is never silently overwritten.

Attachments cannot be edited in place: `attach_text` with `replace: true` uploads the new file and then
removes the old one. If the old one cannot be removed (a guest may add files but not remove someone
else's), both stay and the answer says so.

### Output

Results are compact JSON meant for a model, not the REST DTOs: issues by key, people by name, enums
by name, no colours or ranks, nothing null. Timestamps are in the user's time zone (from their
profile), so "Monday" in the answer is the user's Monday. Changes return the new state, its
`version`, and a link to it in the app.

On a Windows development machine they come out in UTC: the repo builds with
`InvariantGlobalization`, and without ICU Windows cannot resolve IANA zone ids such as
`Europe/Amsterdam`. The Linux images read `/usr/share/zoneinfo` directly and are not affected.

## Boundaries

- **Writes are the app's writes.** Every change goes through the same command as its REST endpoint
  (`IssueCommands`, `ProjectCommands`, `DocumentCommands`, `AttachmentCommands`, `CommentCommands`), so
  it needs the same team permission (editing needs `Write`; commenting and attaching a file need
  `Comment`, as in the app; editing a comment needs to be its author), is
  validated the same way, is recorded in the activity log as the user, and appears live in open
  browsers. Write tools are annotated as such, so MCP clients ask the user before calling them.
- **Nothing is deleted or archived.** No tool deletes or archives issues, projects, documents or
  comments. Replacing an attachment removes the file it replaces, and nothing else.
- **Retries don't duplicate.** An identical issue title from the same user in the same team, or an
  identical comment from the same user on the same issue, within 10 minutes returns the existing one,
  unless the call passes `allowDuplicate`.
- **The user's permissions, never more.** Every query is scoped through `ITeamAccess`, like the REST
  endpoints. A guest's assistant sees and does what the guest can.
- **MCP tokens stay on `/mcp`.** A token issued to an assistant carries the MCP resource as its
  audience. `/mcp` accepts only those tokens, and the rest of the API (REST and realtime) refuses
  them, so approving an assistant hands it the tools on this endpoint and nothing else.
- **Deactivation is immediate.** `/mcp` checks the account is still active on every call, so a
  deactivated user's assistant stops at once instead of when its access token expires.
- **Rate limited** per user: 120 calls a minute. The limiter runs after authentication, so the users
  of one hosted assistant do not share a budget.
- **Stateless.** Every call carries its own bearer token. Nothing about a caller is kept between calls.

## Adding a tool

Reads go straight to the database through `McpReader`. Writes must go through the same command service
as the REST endpoint that makes that change (see `Endpoints/IssueCommands.cs` and
`Endpoints/WorkspaceCommands.cs`), never a second copy of it, so permissions, validation, the activity
log and realtime updates cannot drift apart. A tool that replaces text takes a `version` and checks it
with `McpReader.RequireCurrent`.


Add a method to one of the `[McpServerToolType]` classes in `src/Planner.Api/Mcp/`, or a new class
registered in `McpSetup.AddPlannerMcp`. Start every query from `McpReader.ReadableTeamIdsAsync`, return
`McpReader.Serialize(...)`, and throw `McpException` with a message the model can act on. Add the
tool's name to the list in `tests/Planner.Auth.Checks/McpEndpointChecks.cs`.
