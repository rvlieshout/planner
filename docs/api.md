# API reference

Base URL `http://localhost:8080`. Everything under `/api/v1` requires
`Authorization: Bearer <access_token>`. The live, generated reference is at `/scalar`; the OpenAPI 3.1
document is at `/openapi/v1.json`.

## Conventions

**Enums travel as names.** `"priority": "Urgent"`, not `1` — over REST and over SignalR alike.

**Paging.** List endpoints that can grow take `?page=1&pageSize=50` (max 200) and answer with an
envelope:

```json
{ "items": [ … ], "page": 1, "pageSize": 50, "totalCount": 137, "totalPages": 3, "hasNext": true }
```

Short, bounded lists — a team's members, workflow states, a project's milestones — return a plain
array.

**PATCH is a true patch.** Omit a field and it is untouched. Send it as `null` and it is cleared.

```jsonc
PATCH /api/v1/issues/{id}
{ "priority": "Urgent" }      // title, estimate, milestone all survive
{ "estimate": null }          // estimate is cleared
{ "assigneeId": null }        // unassigned
```

**Repeated query parameters are OR sets, different parameters are AND.**
`?priority=Urgent&priority=High&assigneeId=<guid>` means *(Urgent or High) and assigned to that
person*. Labels are the exception: every requested label must be present, which is what a label
filter on a board means.

**Errors are RFC 9457 problem details.**

```json
{
  "type": "https://tools.ietf.org/html/rfc9110#section-15.5.1",
  "title": "One or more validation errors occurred.",
  "status": 400,
  "errors": { "key": ["key must be 8 characters or fewer."], "name": ["name is required."] }
}
```

| Status | When |
| --- | --- |
| `400` | Validation failed, or a request references something that does not exist |
| `401` | Missing or expired token |
| `403` | Authenticated, a member, but the action needs more authority |
| `404` | Does not exist, **or** you cannot see it |
| `409` | Unique-constraint conflict, a guard tripped (last lead, state in use), or a stale write |
| `429` | Token endpoint rate limit |

Validation accumulates: every problem with a request comes back at once, not one per round trip.

## Auth

| | |
| --- | --- |
| `POST /connect/token` | Password or refresh-token grant. Form-encoded. |
| `GET \| POST /connect/userinfo` | Claims about the current subject |
| `GET /api/v1/me` | Profile, organisation role and team memberships — the first call a client makes |
| `PATCH /api/v1/me` | Update your own display name, avatar, timezone |
| `POST /api/v1/me/password` | Change your own password (needs the current one) |

## Users

| | | Requires |
| --- | --- | --- |
| `GET /api/v1/users` | `?search=&includeInactive=&page=` — for assignee and lead pickers | any user |
| `GET /api/v1/users/{id}` | One user with their role | any user |
| `POST /api/v1/users` | Create an account | admin |
| `PATCH /api/v1/users/{id}` | Profile, `role`, `isActive` | admin |
| `POST /api/v1/users/{id}/password` | Reset without the current password | admin |
| `DELETE /api/v1/users/{id}` | Deactivate; authored content is kept | admin |

## Teams

| | | Requires |
| --- | --- | --- |
| `GET /api/v1/teams` | `?includeArchived=` — teams you can see | Read |
| `GET /api/v1/teams/{id}` | | Read |
| `POST /api/v1/teams` | Creates the six default workflow states and makes you its lead | admin |
| `PATCH /api/v1/teams/{id}` | | Administer |
| `POST /api/v1/teams/{id}/archive` · `/restore` | | Administer |
| `GET /api/v1/teams/{id}/members` | | Read |
| `POST /api/v1/teams/{id}/members` | `{ userId, role }` | Administer |
| `PATCH /api/v1/teams/{id}/members/{userId}` | `{ role }` | Administer |
| `DELETE /api/v1/teams/{id}/members/{userId}` | | Administer |

New teams start with **Backlog · Todo · In Progress · In Review · Done · Canceled**, with `Todo` as
the default for new issues.

### Workflow states and labels

| | | Requires |
| --- | --- | --- |
| `GET /api/v1/teams/{id}/states` | Board columns, in order | Read |
| `POST /api/v1/teams/{id}/states` | | Administer |
| `PATCH /api/v1/teams/{id}/states/{stateId}` | Setting `isDefault` clears the previous default | Administer |
| `DELETE /api/v1/teams/{id}/states/{stateId}` | 409 while issues still sit in it | Administer |
| `GET /api/v1/teams/{id}/labels` | Team labels plus organisation-wide ones | Read |
| `POST /api/v1/teams/{id}/labels` | | Administer |
| `GET /api/v1/labels` | Everything you can use | any user |
| `POST /api/v1/labels` | Organisation-wide label | admin |
| `PATCH \| DELETE /api/v1/labels/{labelId}` | | Administer, or admin for org labels |

## Projects and milestones

| | | Requires |
| --- | --- | --- |
| `GET /api/v1/projects` | `?teamId=&status=&search=&includeArchived=&page=` | Read |
| `GET /api/v1/projects/{id}` | | Read |
| `POST /api/v1/projects` | | Write |
| `PATCH /api/v1/projects/{id}` | | Write |
| `POST /api/v1/projects/{id}/archive` · `/restore` | | Write |
| `DELETE /api/v1/projects/{id}` | Issues survive and stay in the team | Administer |
| `GET \| POST /api/v1/projects/{id}/milestones` | | Read / Write |
| `GET \| PATCH \| DELETE /api/v1/milestones/{id}` | | Read / Write / Write |

Every project and milestone carries a computed rollup, counted from issues at read time:

```json
"progress": { "total": 12, "completed": 5, "started": 3, "canceled": 1, "ratio": 0.4545 }
```

`ratio` is completed over non-canceled work. Setting a project's status to `Completed` stamps
`completedAt`; moving it away clears it again.

## Documents

Markdown project documentation — specs, briefs, decision records. Separate from a project's
`description` so documents can be listed, searched, archived and reassigned on their own.

| | | Requires |
| --- | --- | --- |
| `GET /api/v1/documents` | `?teamId=&projectId=&search=&includeArchived=&page=` — summaries, no bodies | Read |
| `GET /api/v1/documents/{id}` | Full markdown body | Read |
| `POST /api/v1/documents` | `{ teamId, projectId?, title, content }` | Write |
| `PATCH /api/v1/documents/{id}` | Records who last edited it | Write |
| `POST /api/v1/documents/{id}/archive` · `/restore` | | Write |
| `DELETE /api/v1/documents/{id}` | | Administer |
| `GET /api/v1/projects/{projectId}/documents` | | Read |

## Issues

| | | Requires |
| --- | --- | --- |
| `GET /api/v1/issues` | The filter below | Read |
| `GET /api/v1/issues/{id}` | Sub-issues, relations, attachments, comment count | Read |
| `GET /api/v1/issues/by-key/{key}` | `ENG-42` | Read |
| `POST /api/v1/issues` | | Write |
| `PATCH /api/v1/issues/{id}` | | Write |
| `POST /api/v1/issues/{id}/move` | Board drag-and-drop | Write |
| `POST /api/v1/issues/{id}/archive` · `/restore` | | Write |
| `DELETE /api/v1/issues/{id}` | | Administer |
| `GET /api/v1/issues/{id}/activity` | Audit trail for this issue | Read |

### Filtering

| Parameter | |
| --- | --- |
| `teamId`, `projectId`, `milestoneId`, `parentId` | Scope |
| `stateId` | Repeatable — specific columns |
| `stateType` | Repeatable — `Backlog`, `Unstarted`, `Started`, `Completed`, `Canceled`. Survives column renames. |
| `assigneeId` | Repeatable |
| `unassigned=true` | Overrides `assigneeId` |
| `labelId` | Repeatable, **AND**-combined |
| `priority` | Repeatable — `None`, `Urgent`, `High`, `Medium`, `Low` |
| `search` | Trigram match on title and description. A value shaped like `ENG-42` is treated as a key lookup instead. |
| `dueBefore`, `dueAfter` | `YYYY-MM-DD` |
| `updatedSince` | ISO 8601. Delta sync for a client that was offline. |
| `topLevelOnly=true` | Hide sub-issues |
| `includeArchived=true` | Default is to hide them |
| `sort` | `-updatedAt` (default), `updatedAt`, `board`, `priority`, `dueDate`, `createdAt`, `-createdAt`, `number`, `sortOrder` |

`sort=board` orders by column position then rank — the exact order a board renders.

Only these sort keys are accepted. An unrecognised value falls back to the default rather than
reaching the query builder.

### Creating and moving

```jsonc
POST /api/v1/issues
{
  "teamId": "…",
  "title": "Wire up the reconnect banner",
  "priority": "High",
  "projectId": "…",          // optional
  "milestoneId": "…",        // optional; must belong to that project
  "parentId": "…",           // optional; must be in the same team
  "assigneeId": "…",         // optional; must be a member of the team
  "labelIds": ["…"],
  "estimate": 3,
  "dueDate": "2026-09-30"
}
```

Omit `stateId` and the issue lands in the team's default state. The response carries the assigned key
(`ENG-42`).

```jsonc
POST /api/v1/issues/{id}/move
{ "stateId": "…", "afterIssueId": "…", "beforeIssueId": "…" }
```

Rank is fractional: dropped between two neighbours, an issue takes the midpoint of their `sortOrder`,
so a drag writes one row. Give only `afterIssueId` or only `beforeIssueId` to land at an edge, or
neither to append to the column.

Lifecycle stamps follow the target state's **type**, not its name: moving into anything typed
`Started` sets `startedAt`, `Completed` sets `completedAt`, `Canceled` sets `canceledAt`, and moving
back out clears them. Rename your columns freely.

### Comments, attachments, relations

| | | Requires |
| --- | --- | --- |
| `GET \| POST /api/v1/issues/{id}/comments` | `{ body, parentCommentId? }` — one level of threading | Read / **Comment** |
| `PATCH /api/v1/comments/{id}` | Author only | author |
| `DELETE /api/v1/comments/{id}` | Author, or a team lead moderating | author / Administer |
| `POST /api/v1/issues/{id}/attachments` | `{ fileName, storageUri, contentType?, sizeBytes? }` — metadata for a file held elsewhere | **Comment** |
| `POST /api/v1/issues/{id}/files?fileName=` | The raw bytes as the request body, up to 20 MiB | **Comment** |
| `GET /api/v1/attachments/{id}/content` | Downloads bytes this server holds | Read |
| `DELETE /api/v1/attachments/{id}` | Uploader, or any team member | uploader / Write |
| `POST /api/v1/issues/{id}/relations` | `{ targetIssueId, type }` — `Related`, `Blocks`, `Duplicates` | Write |
| `DELETE /api/v1/issues/{id}/relations/{relationId}` | | Write |

An attachment goes on either way. `POST …/attachments` stores **metadata only** — put the bytes on your
own share or object store and the resulting location in `storageUri`. `POST …/files` sends the bytes
themselves; the API writes them under `Attachments__Path`, outside the web root, and sets
`storageUri` to `planner-attachment:{id}`, which is how a client tells the two apart. Downloading
through `GET /attachments/{id}/content` re-checks the issue's team permission.

`Attachments__Path` must be an absolute, writable directory in any container deployment — the default
lives under the application folder, which the image's non-root user cannot create. Back it up with the
database.

Relations are stored once and shown from both ends: create "A blocks B" and issue B reports it as an
inbound relation with `"isOutgoing": false`. Cross-team relations are allowed, provided you can read
both teams.

## Activity

| | |
| --- | --- |
| `GET /api/v1/activity` | `?teamId=&projectId=&since=&page=` across the teams you can read |
| `GET /api/v1/issues/{id}/activity` | One issue's history |

Each row has an `action`, the `actor`, and a `data` object describing the change:

```json
{ "action": "state_changed", "actor": { "displayName": "Dana Developer" },
  "data": { "from": "Todo", "to": "In Progress" }, "createdAt": "2026-08-27T15:44:45Z" }
```

Actions: `created`, `updated`, `archived`, `restored`, `deleted`, `state_changed`,
`assignee_changed`, `priority_changed`, `labels_changed`, `commented`, `relation_added`,
`relation_removed`, `attachment_added`, `member_added`, `member_removed`, `member_role_changed`.

## Health

| | |
| --- | --- |
| `GET /health/live` | Process is up. No dependency checks — this is the container's own probe. |
| `GET /health/ready` | Database reachable. Use this for load-balancer readiness. |

Both are anonymous, as are `/openapi/v1.json` and `/scalar` — the docs UI has to be able to load the
document it renders. Every endpoint they describe still needs a token.
