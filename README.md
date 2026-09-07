# Planner

An on-premises project and issue tracker in the shape of Linear: teams own projects, projects run to
milestones, and work happens as issues on a board. Everything lives on your own hardware — Postgres
for storage, a single .NET container for the API, no outbound calls.

Two halves live here: the **backend** (database, HTTP API, realtime feed) and the **Avalonia desktop
client**, which shares the DTOs in `Planner.Contracts` so both ends are checked by the same compiler.
The client ships and updates itself through Velopack, from a feed the API serves.

## What is here

| Piece | Choice |
| --- | --- |
| Runtime | .NET 10, minimal APIs |
| Database | PostgreSQL 17, EF Core 10 (Npgsql) |
| Identity | ASP.NET Core Identity, users and roles in the same database |
| Tokens | OpenIddict 7 — self-hosted OAuth 2.0 / OIDC, password + refresh grants, plain JWTs |
| Realtime | SignalR hub at `/hubs/planner`, strongly typed against a shared interface |
| Client | Avalonia 12 on .NET 10, MVVM with compiled bindings, Lucide icons |
| Client updates | Velopack — delta packages, feed served by the API at `/updates` |
| Docs | OpenAPI 3.1 at `/openapi/v1.json`, Scalar UI at `/scalar` |
| Packaging | Docker Compose: `db` + `api` (+ optional pgAdmin) |

71 HTTP operations across teams, members, workflow states, labels, projects, milestones, documents,
issues, sub-issues, relations, comments, attachments, users and the activity feed.

## Run it

Docker Desktop is the only prerequisite.

### While developing: Aspire

```bash
dotnet run --project src/Planner.AppHost      # or `aspire run` from the repository root
```

Or set `Planner.AppHost` as the startup project in Visual Studio or Rider and press Start.

One command brings up Postgres in a container, waits for it, runs the API against it — migrations,
OAuth client and bootstrap owner included — and opens a dashboard with the logs, traces and endpoints
of everything it started. The dashboard is at <https://localhost:17200> (or
<http://localhost:15200> if you have not trusted the development certificate with
`dotnet dev-certs https --trust` — pick the `http` launch profile for that), and the login link is
printed on startup.

The desktop client is registered there too, as a resource you start explicitly: press **Start** on
`client` in the dashboard and it launches pointed at whatever port the API landed on. It is not
launched automatically, because a window appearing every time you start the API is rarely what you
wanted.

`Ctrl+C` stops everything. The database keeps its data in a named volume between runs; delete the
`planner-aspire-pgdata` volume to go back to a clean seed.

### As deployed: compose

```bash
cp .env.example .env          # then edit the three change-me passwords
docker compose up -d --build
```

The API applies its own migrations, registers the OAuth client and creates the bootstrap owner on
first start. Watch it come up with `docker compose logs -f api`.

- API: <http://localhost:8080>
- Interactive docs: <http://localhost:8080/scalar>
- Health: <http://localhost:8080/health/ready>
- pgAdmin (optional): `docker compose --profile tools up -d` → <http://localhost:5050>

Set `PLANNER_SEED_DEMO=true` in `.env` to start with a sample team, project, milestones and issues —
useful while building the client.

### First call

```bash
curl -s -X POST http://localhost:8080/connect/token \
  -d grant_type=password \
  -d client_id=planner-desktop \
  -d username=owner@planner.local \
  -d 'password=<PLANNER_OWNER_PASSWORD>' \
  -d 'scope=openid profile roles offline_access planner.api'
```

Then send `Authorization: Bearer <access_token>` to any endpoint. `tools/planner.http` has a worked
sequence for VS Code / Rider.

`docker-compose.yml` describes the deployed system and stays the reference for it; the app host
describes the one you develop against. They deliberately do not share a database volume.

## Run it without Docker

```bash
docker compose up -d db                        # or point at your own Postgres
cd src/Planner.Api
dotnet user-secrets set "Planner:Seed:OwnerPassword" "SomethingLong123!"
dotnet run
```

`appsettings.Development.json` targets `localhost:5432`, keeps certificates in `./keys`, allows plain
HTTP and seeds demo data.

## The desktop client

```bash
dotnet run --project src/Planner.Client
```

Sign in with the same bootstrap owner. `PLANNER_SERVER_URL` overrides the stored server address for one
process, which is how the Aspire app host points the client at the API and how a managed rollout can
aim a machine at its own server without provisioning a settings file first. The client remembers the server and resumes the session on the
next launch. The sidebar carries My Issues, the team board and one row per project; Ctrl+N files work
into the current team, and everything stays live over SignalR.

It is built as a desktop application rather than a page in a window frame: menu bar, toolbar and status
bar, a sidebar you can drag or collapse, dense selectable lists where Enter and double-click open the
selected issue, drag-and-drop between board columns and My Issues groups, and the issue form as a real
modal dialog. See [docs/desktop-client.md](docs/desktop-client.md) for the shortcut table and the
design system behind it.

The look comes from [AtomUI](https://github.com/AtomUI/AtomUI), an Ant Design component system for
Avalonia. The client uses its controls throughout and reads its design tokens directly, so both theme
variants, and the density the window is tuned to, follow from a handful of token overrides in
`App.axaml.cs` rather than from per-control styling.

To build a release of it:

```powershell
./build/release.ps1 -Version 1.1.0
```

That produces an installer, a delta package and a feed index in `./releases`, which compose mounts
into the API at `/updates`. Installed clients check that feed at startup and every four hours, download
what they find, and offer a restart — whether or not anyone has signed in.
See [docs/releasing.md](docs/releasing.md).

## Layout

```
src/
  Planner.Domain          entities and roles
  Planner.Contracts       DTOs, enums and the SignalR interface — no dependencies, shared by both ends
  Planner.Infrastructure  DbContext, EF configurations, migrations, seeding
  Planner.Api             minimal API endpoints, authorization, OpenIddict, the hub, the update feed
  Planner.Client          Avalonia desktop client
  Planner.AppHost         Aspire app host — the development stack as one command
build/release.ps1         packages and publishes a client release
releases/                 the Velopack update feed (git-ignored contents)
docs/                     architecture, database, roles, API, realtime, client and release references
tools/planner.http        example requests
```

## Documentation

| Document | What it covers |
| --- | --- |
| [docs/architecture.md](docs/architecture.md) | Layering, the decisions worth knowing about, and why |
| [docs/database.md](docs/database.md) | Schema, relationships, indexes and the conventions behind them |
| [docs/roles-and-permissions.md](docs/roles-and-permissions.md) | Organisation roles, team roles, and the full permission matrix |
| [docs/api.md](docs/api.md) | Endpoint reference, filtering, paging, PATCH semantics, error shapes |
| [docs/realtime.md](docs/realtime.md) | Hub contract, group model, and a client sample |
| [docs/desktop-client.md](docs/desktop-client.md) | Client architecture, where it stores things, what is not built yet |
| [docs/releasing.md](docs/releasing.md) | Packaging, distribution, channels, rollback, signing |

## Configuration

Every setting binds from environment variables using `__` as the separator
(`Planner__Auth__AccessTokenMinutes=30`).

| Key | Default | Meaning |
| --- | --- | --- |
| `ConnectionStrings__Planner` | — | Postgres connection string. Required. |
| `Planner__Database__AutoMigrate` | `true` | Apply pending migrations on start. Turn off where a DBA runs them. |
| `Planner__Auth__KeyDirectory` | `/var/lib/planner/keys` | Where token certificates live. Must be a persistent volume. |
| `Planner__Auth__KeyPassword` | — | Protects those certificates at rest. |
| `Planner__Auth__AccessTokenMinutes` | `60` | Access token lifetime. |
| `Planner__Auth__RefreshTokenDays` | `14` | Refresh token lifetime. |
| `Planner__Auth__AllowInsecureHttp` | `false` | Allow plain HTTP on the token endpoint. True behind a TLS proxy. |
| `Planner__Auth__AllowedOrigins__0` | — | CORS origins, one per index. Not needed by the desktop client. |
| `Planner__Seed__OwnerEmail` | `owner@planner.local` | Bootstrap owner account. |
| `Planner__Seed__OwnerPassword` | — | Set it, or no owner is created. Minimum 12 characters. |
| `Planner__Seed__SeedDemoData` | `false` | Populate an empty database with sample content. |
| `Planner__Updates__Enabled` | `true` | Serve the desktop client's update feed. |
| `Planner__Updates__Directory` | `/var/lib/planner/updates` | Where release files live. Bound to `PLANNER_UPDATE_DIR` on the host. |
| `Planner__Updates__RequestPath` | `/updates` | Public path of the feed. Anonymous, deliberately. |

## Operational notes

- **Back up two things**: the Postgres volume and the keys volume. Losing the keys volume invalidates
  every issued token; users simply sign in again, but it is avoidable.
- **TLS** is expected to terminate at a reverse proxy. The container speaks HTTP on 8080.
- **Migrations** ship inside the image. `AutoMigrate=false` plus
  `dotnet ef migrations script --idempotent` gives you a script to hand to a DBA instead.
- **Deleting** is deliberately rare. Teams, projects, issues and documents archive; users deactivate.
  Hard deletes exist but need team-lead or admin authority.
- **The update feed is anonymous** and served from `PLANNER_UPDATE_DIR`. That is intentional: a client
  must be able to fetch a fix for a release that broke sign-in. Keep old packages — clients that have
  been offline need the ones in between.
