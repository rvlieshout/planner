# Architecture

## Shape

Four projects, dependencies pointing one way only.

```
Planner.Domain          entities, enums, organisation roles
      ^          ^
      |          |
Planner.Contracts      Planner.Infrastructure     DbContext, EF configurations, migrations, seeding
      ^                        ^
      |                        |
      +------ Planner.Api -----+                  endpoints, authorization, OpenIddict, SignalR hub
```

`Planner.AppHost` sits outside that graph. It references the API and the clients, but only to start
them: it is the Aspire description of the development stack — Postgres, the API wired to it, the web
client on Vite, and the desktop client on explicit start — and nothing in the running system depends
on it.

`Planner.Contracts` holds every request and response DTO plus `IPlannerClient`, the SignalR
interface. It was the one project the removed Avalonia client referenced, which is why it has no
dependencies of its own: a .NET client binds to hub method names and payload shapes at compile time
instead of by convention.

The web client cannot reference it. `client/src/lib/api/types.ts` is a hand-written mirror instead,
ordered to match the C# files file for file. That is a real seam, and the honest way to describe it
is: the compiler checks one client, and `svelte-check` plus this repository's discipline check the
other. Generating those types from `/openapi/v1.json` would close it, at the cost of requiring a
running API to build the client.

There is no service or repository layer. Endpoints talk to `PlannerDbContext` directly. For an
application whose business rules are mostly "check the caller's permission on this team, then write a
row", a service layer would be a second name for the same code. What *is* factored out is anything
that would otherwise be repeated inconsistently: permission resolution (`ITeamAccess`), realtime
publishing (`IRealtimeNotifier`), audit writes (`IActivityLog`), issue numbering
(`IIssueNumberGenerator`) and DTO projections (`Mapping`).

## Decisions worth knowing

### Auth is self-hosted, and every token belongs to a person

OpenIddict runs inside the API as both authorization server and resource server. There is no external
identity provider to install, which is the point for an on-prem box.

Two grants are enabled: `password` and `refresh_token`, for two registered public clients —
`planner-desktop` and `planner-web`, separate so they can be told apart in the logs and revoked
independently.

Authorization-code + PKCE is the better pattern in general, and the web client removes the argument
that there is no browser to redirect through. What it does not remove is the rest of the work: an
interactive login page inside the API, cookie authentication to consent from, and a migration for
every installed desktop client. Both of this system's clients are first-party and the API is the only
thing holding the passwords anyway, so the password grant is still what ships. The upgrade path is
real and unchanged —
see [roles-and-permissions.md](roles-and-permissions.md#upgrading-to-authorization-code--pkce).

Deliberately *not* enabled: `client_credentials`. Every authorization decision in this system starts
from "which teams is this user in", and a token with no user behind it has no answer. Service
integrations get a real (deactivatable, auditable) user account instead.

Access tokens are unencrypted JWTs (`DisableAccessTokenEncryption`). A sidecar, a reverse proxy or a
support engineer with `jwt.io` can read them; nothing sensitive goes in them beyond subject, name,
email and role.

### Certificates persist on a volume

OpenIddict's development certificates live in the machine X.509 store, which is empty again after a
container restart — every restart would invalidate every token. `ServerCertificates` generates a
signing and an encryption certificate once into `Planner:Auth:KeyDirectory` and reuses them. Data
protection keys go to the same volume for the same reason.

### Permissions are computed, not stored

A caller's authority over a team is a function of two things: their organisation role (one Identity
role per user) and their team role (`TeamMember.Role`). `TeamAccess` folds them into a single
`TeamPermission` ladder — `None < Read < Comment < Write < Administer` — and every endpoint asks one
question: "do I have at least X on this team?".

Nothing is denormalised onto rows, so a role change takes effect on the next request rather than
after a background rebuild. Memberships are cached per request, because a list endpoint checks one
team per row.

A team the caller cannot see at all answers **404**, not 403. A 403 would confirm that a private team
by that id exists.

### Realtime is a fan-out over groups, not a message bus

Every mutation publishes exactly one event to exactly one group:

- team-wide traffic (issues, projects, milestones, documents, labels, states, membership) → `team:{id}`
- issue-local traffic (comments, attachments, relations) → `issue:{id}`, joined on demand

One group per event means a connection in several groups never receives the same change twice, and a
board with 200 open issues does not stream every comment typed in the team.

Publishing happens after `SaveChanges`, never before: a client must not be told about a write that
then rolls back. This is in-process — one API instance. Scaling out needs a SignalR backplane
(Redis) plus an outbox to keep publishes exactly-once; both are listed under Limits below.

### The audit trail is a table, not a log file

`activity_events` is append-only, carries a `jsonb` payload of what changed, and is denormalised with
`team_id` / `project_id` / `issue_id` so the feed can be filtered without joins. It backs the
per-issue history pane, and gives a client that was offline something to replay.

### PATCH means patch

`Optional<T>` in the contracts distinguishes "field absent from the body" from "field explicitly set
to `null`". Without it, `PATCH {"assigneeId": null}` and `PATCH {"title": "x"}` are indistinguishable
at the model level and clearing a field becomes impossible. A single `JsonConverter` handles it; the
endpoints read `request.Field.Or(entity.Field)`.

### Sort order is fractional

Board position is a `double`. An issue dropped between two neighbours takes the midpoint of their
ranks, so a drag writes one row instead of renumbering a column. After roughly 50 consecutive
midpoint inserts in the same gap the precision runs out; a periodic renumber job is the standard
answer and is not implemented here.

### Issue numbers come from the database

`ENG-42` is `team.key` plus a per-team counter. The counter is bumped with a single
`UPDATE teams SET issue_counter = issue_counter + 1 ... RETURNING`, inside the request's transaction.
Read-then-write in application code would hand two concurrent creates the same number, and the unique
`(team_id, number)` index would then reject one of them.

### Concurrency uses `updated_at`

`updated_at` is an EF concurrency token on the mutable aggregates, so every UPDATE carries
`WHERE updated_at = @original` and a stale write fails loudly instead of silently overwriting a change
the client never saw. `PlannerExceptionHandler` turns that into a 409. Postgres' `xmin` would be the
more idiomatic token, but the Npgsql provider dropped `UseXminAsConcurrencyToken` in v10.

## Request flow

```
HTTP request
  └─ SignalRAuthenticationMiddleware   lifts ?access_token= into the Authorization header for /hubs
  └─ CORS, rate limiter                the token endpoint gets its own per-IP budget
  └─ Authentication                    OpenIddict validation, local server
  └─ Authorization                     fallback policy: authenticated by default; opt out explicitly
  └─ Endpoint handler
       ├─ ApiResults.RequireTeamAsync  permission gate, returns the response to send when denied
       ├─ Validation                   accumulates every problem before answering
       ├─ DbContext                    read, mutate, single SaveChangesAsync (audit row included)
       └─ IRealtimeNotifier            publish after the commit
  └─ PlannerExceptionHandler           unique violation → 409, FK violation → 400, stale write → 409
```

## Limits, stated plainly

These are deliberate omissions, not oversights:

- **Single instance.** No SignalR backplane and no outbox, so running two API replicas would split the
  realtime feed. Redis backplane + a transactional outbox is the fix.
- **Attachment storage is a directory, not an object store.** `POST /issues/{id}/files` writes bytes
  under `Attachments__Path`, outside the web root and behind the issue's team permission; an
  attachment can also be pure metadata pointing at a share. Several API instances would have to share
  that directory, and deleting an attachment does not purge its bytes.
- **No notifications.** No email, no per-user inbox, no subscriber list on issues.
- **No cycles/sprints.** Linear's cycles are absent; projects and milestones cover the planning need.
  Adding them is a table plus a nullable FK on `issues`.
- **Search is trigram `ILIKE`.** Fine to six figures of issues. Beyond that, a generated `tsvector`
  column with a GIN index is the next step.
- **Views and saved filters** are computed client-side from the issue filter; nothing is persisted.
