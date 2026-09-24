# API reference

Base URL `http://localhost:8080`. Everything under `/api/v1` requires
`Authorization: Bearer <access_token>`. The live, generated reference is at `/scalar`; the OpenAPI 3.1
document is at `/openapi/v1.json`.

## Conventions

**Enums travel as names.** `"priority": "Urgent"`, not `1` — over REST and over SignalR alike.

**Ids are base58.** `"id": "1CFM9HDkWavHzjEZuAP3qG"` — 22 characters, everywhere an id appears: in a
body, in a path, in a query string and in a SignalR payload. The database still stores uuids and the
API decodes on the way in, so the two are the same value in different clothes:

```
019205f7-0c3e-7b6a-9f21-4d8c5e6a1b37  ->  1CFM9HDkWavHzjEZuAP3qG
```

The alphabet is Bitcoin's — the digits and letters minus `0`, `O`, `I` and `l` — so an id survives a
double-click, a URL and a read-aloud unchanged. Treat one as opaque: compare them, do not build them.
The canonical hyphenated form is still accepted on input, so an old link or a saved curl command
keeps working, but nothing the API writes uses it.

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
`?priority=Urgent&priority=High&assigneeId=<id>` means *(Urgent or High) and assigned to that
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
| `PATCH /api/v1/projects/{id}` | Changing `rank` — the team's project order — needs Administer | Write |
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
| `DELETE /api/v1/issues/{id}` | Permanently, archived or not. Comments, files (bytes included), relations and inbox entries go with it; sub-issues become top-level; its history stays | Administer |
| `GET /api/v1/issues/{id}/activity` | Audit trail for this issue | Read |
| `GET /api/v1/issues/{id}/subscribers` | People following the issue | Read |
| `PUT /api/v1/issues/{id}/subscribers/{userId}` | Follow — yourself with Read, anyone else with Write | Read / Write |
| `DELETE /api/v1/issues/{id}/subscribers/{userId}` | Unfollow — same rule | Read / Write |

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
| `sort` | `-updatedAt` (default), `updatedAt`, `board`, `priority`, `dueDate`, `createdAt`, `-createdAt`, `number`, `rank` (`sortOrder` is accepted as an older name for it) |

`sort=board` orders by the column's rank, then the issue's rank, then its number — the exact order a
board renders.

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

The issue lands after `afterIssueId` and takes a `rank` key between that issue's and the next one's in
the column, so a drag writes one row. The next one is read from the database rather than taken from
`beforeIssueId`, so a drop made against a stale view still lands in a real gap. Give only
`beforeIssueId` to land at the top, or neither to append to the column. An explicit `rank` is used only
when neither anchor is given.

### Ordering

Every hand-ordered list — a board column, a team's workflow states and projects, a project's
milestones — is ordered by a `rank` string such as `a0`, `a0V` or `d0012`. Sort them **ordinally**
(byte by byte; `<` in JavaScript, `StringComparer.Ordinal` in .NET), never with a locale-aware
comparison: `a0V` sorts before `a0a`. Between any two keys there is always another, so reordering one
row never renumbers its neighbours.

Where the API takes a `rank` directly — `PATCH` on a project, milestone, workflow state or issue, and
`POST /teams/{id}/states` — it must be a well-formed key of at most 128 characters. A client makes one
by taking a key between the two rows it is placing between; `client/src/lib/rank.ts` and
`Planner.Domain.Common.Rank` implement the same function. Omitted on create, a row goes to the end.

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

Deleting an attachment also deletes its server-owned file. If file deletion fails, the API returns
an error and keeps the attachment record for retry. Missing files can still have their records removed.
External links are detached only; Planner does not delete files at external locations.

`Attachments__Path` must be an absolute, writable directory in any container deployment — the default
lives under the application folder, which the image's non-root user cannot create. Back it up with the
database.

An archived issue is read-only. Updating or moving it, commenting on it or editing and deleting its
comments, adding or removing its files, and relating to or from it all answer `409 Conflict` until it is
restored; so does filing a sub-issue under it, moving another issue beneath it, or following it — for
yourself or for someone else. Reading it, seeing and removing its followers, restoring it and a team
lead's permanent delete still work, and a relation to an archived issue can still be removed from the
live end.

Relations are stored once and shown from both ends: create "A blocks B" and issue B reports it as an
inbound relation with `"isOutgoing": false`. Cross-team relations are allowed, provided you can read
both teams.

## Activity

| | |
| --- | --- |
| `GET /api/v1/activity` | `?teamId=&projectId=&since=&page=` across the teams you can read |
| `GET /api/v1/issues/{id}/activity` | One issue's history |
| `DELETE /api/v1/activity` | `?projectId=` or `?teamId=`, optionally `&olderThanDays=` — administrators only |

Each row has an `action`, the `actor`, a `data` object describing the change, and — for anything that
happened to an issue — an `issue` reference naming it, so a feed across teams needs no request per row:

```json
{ "action": "state_changed", "actor": { "displayName": "Dana Developer" },
  "data": { "from": "Todo", "to": "In Progress" }, "createdAt": "2026-08-27T15:44:45Z",
  "issue": { "id": "…", "key": "ENG-42", "title": "Fix login" } }
```

`issue` is null for events outside an issue, and for events of an issue that has since been deleted.

Actions: `created`, `updated`, `archived`, `restored`, `deleted`, `state_changed`,
`assignee_changed`, `priority_changed`, `labels_changed`, `project_changed`, `milestone_changed`,
`commented`, `relation_added`, `relation_removed`, `attachment_added`, `member_added`,
`member_removed`, `member_role_changed`, `activity_purged`.

On an issue, `updated` carries `{ "fields": ["title", "description", …], "title": { "from", "to" } }`
for the edits that have no verb of their own; `project_changed` and `milestone_changed` carry names
as well as ids (`from`, `to`, `fromId`, `toId`); `labels_changed` carries `added` and `removed` and is
only recorded when the set actually changed.

### Deleting history

An organisation administrator can delete a project's or a team's history: all of it, or only what is
older than `olderThanDays` (1–36500). Exactly one of `projectId` and `teamId` is required. A project
purge removes the events recorded against that project, including those of its issues; a team purge
removes every event of the team. Inbox entries that pointed at deleted events go with them.

The purge leaves a row of its own — `activity_purged` on the project or team, with
`{ "olderThanDays": 90, "deleted": 42 }` — written in the same transaction, so the history always says
who cleared it and how much. The response is `{ teamId, projectId, before, deleted }`, where `before` is
the cutoff (null for everything); the same object is pushed to the team as `ActivityPurged`.

## Following and the inbox

Following an issue puts whatever happens to it in your inbox. Filing an issue, being assigned it and
commenting on it follow it automatically; anyone who can read an issue can follow or unfollow it, and
someone with Write can add or remove another person, provided that person can read the team.

Every audit event on an issue produces one notification per follower, except for the person who acted.
They are written in the same transaction as the change, by a save interceptor rather than by each
endpoint (`Planner.Api/Notifications/NotificationInterceptor.cs`), so a committed change is never
missing from an inbox and a rolled-back one never appears in one.

| | |
| --- | --- |
| `GET /api/v1/notifications` | `?unread=true&page=` — your inbox, newest first |
| `GET /api/v1/notifications/status` | `{ "unread": 3 }` |
| `PATCH /api/v1/notifications/{id}` | `{ "read": true }` or `false` |
| `POST /api/v1/notifications/read` | Everything read, or with `?issueId=` everything about one issue |

Each entry is `{ id, issue: { id, key, title }, teamId, event, createdAt, readAt }`, where `event` is
the activity row above. The inbox follows access as it is *now*: someone removed from a team stops
seeing its entries, and they stop counting towards `unread`. Deleting an issue deletes its entries.

## Health

| | |
| --- | --- |
| `GET /health/live` | Process is up. No dependency checks — this is the container's own probe. |
| `GET /health/ready` | Database reachable. Use this for load-balancer readiness. |

Both are anonymous, as are `/openapi/v1.json` and `/scalar` — the docs UI has to be able to load the
document it renders. Every endpoint they describe still needs a token.
