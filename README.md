# Planner

An on-premises project and issue tracker in the shape of Linear: teams own projects, projects run to
milestones, and work happens as issues on a board. Everything lives on your own hardware — Postgres
for storage, a single .NET container for the API, no outbound calls.

Two halves live here: the **backend** (database, HTTP API, realtime feed) and the **web client**, a
SvelteKit application served from the same origin as the API — so there is nothing to install, nothing
to keep updated, and no CORS to configure.

The **Avalonia desktop client** is still in the tree and still builds, but it is frozen: new work goes
into the web client. See [docs/desktop-client.md](docs/desktop-client.md).

## What is here

| Piece | Choice |
| --- | --- |
| Runtime | .NET 10, minimal APIs |
| Database | PostgreSQL 18, EF Core 10 (Npgsql) |
| Identity | ASP.NET Core Identity, users and roles in the same database |
| Tokens | OpenIddict 7 — self-hosted OAuth 2.0 / OIDC, password + refresh grants, plain JWTs |
| Realtime | SignalR hub at `/hubs/planner`, strongly typed against a shared interface |
| Client | SvelteKit 2 / Svelte 5 — static bundle, custom CSS, self-hosted Inter and Lucide |
| Client hosting | Caddy, same origin as the API, at `/app` |
| Legacy client | Avalonia 12 on .NET 10 — frozen; updates through Velopack from `/updates` |
| Docs | OpenAPI 3.1 at `/openapi/v1.json`, Scalar UI at `/scalar` |
| Packaging | Docker Compose: `db` + `api` + `web` (+ optional pgAdmin) |

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
OAuth clients and bootstrap owner included — starts the web client on Vite's development server
pointed at whatever port the API landed on, and opens a dashboard with the logs, traces and endpoints
of everything it started. The dashboard is at <https://localhost:17200> (or
<http://localhost:15200> if you have not trusted the development certificate with
`dotnet dev-certs https --trust` — pick the `http` launch profile for that), and the login link is
printed on startup.

The frozen desktop client is registered there too, as a resource you start explicitly: press **Start**
on `desktop` in the dashboard and it launches pointed at the same API. It is not launched
automatically, because a window appearing every time you start the API is rarely what you wanted.

`Ctrl+C` stops everything. The database keeps its data in a named volume between runs; delete the
`planner-aspire-pgdata` volume to go back to a clean seed.

### As deployed: compose

`docker-compose.yml` is the local, everything-in-one-file stack. The VPS runs
`docker-compose.coolify.yml` instead, from images GitHub Actions publishes to GHCR; see
[docs/deploy-coolify.md](docs/deploy-coolify.md). Both use PostgreSQL 18, and an existing
PostgreSQL 17 volume has to be dumped and restored rather than pointed at: changing the image tag
and mount path does not upgrade the database.

```bash
cp .env.example .env          # then edit the three change-me passwords
docker compose up -d --build
```

The API applies its own migrations, registers the OAuth client and creates the bootstrap owner on
first start. Watch it come up with `docker compose logs -f api`.

- Web client: <http://localhost:8081/app>
- Download website: <http://localhost:8081>
- API: <http://localhost:8080>
- Interactive docs: <http://localhost:8080/scalar>
- Health: <http://localhost:8080/health/ready>
- pgAdmin (optional): `docker compose --profile tools up -d` → <http://localhost:5050>

The `web` container is the front door: it serves the website at `/`, the client at `/app`, and proxies
everything else to the API. The API keeps its own published port because the docs, `tools/planner.http`
and the desktop client address it directly; the browser client never does.

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

## The web client

```bash
cd client
npm install
npm run dev          # http://localhost:5175/app
```

Sign in with the same bootstrap owner. There is no server field: the application was served by the
installation it talks to, and in development Vite proxies `/api`, `/connect` and `/hubs` to
`PLANNER_SERVER_URL` — so in both cases the browser only ever speaks to the origin it came from. That
is what makes CORS, a second hostname and an origin allow-list unnecessary.

The sidebar carries My Issues, the team board and one row per project; `C` files work into the
current team, Ctrl+K opens a command palette listing every shortcut the open screen offers, and
everything stays live over SignalR. Administration
sits at the top of that sidebar for the people entitled to it: **Users & access** for owners and
administrators, and **Teams** — settings and membership, creating and archiving — for them and for the
leads of the teams they lead.

It is built as a desktop application rather than a page in a browser: an application menu, a toolbar
strip and a status bar, a sidebar you can drag or collapse, dense rows where Enter and double-click
open the selected issue, pointer-driven drag-and-drop between board columns and My Issues groups, and
the issue form as a real modal dialog. Projects go the other way — a page rather than a dialog, because
a project is created, then filled in, and its milestones are maintained on the same page. Issues are
addressed by key, so `ENG-42` is a URL you can paste into a chat.

The design system is custom CSS: one file of tokens, no framework, no component library. Inter and
JetBrains Mono are compiled into the bundle and Lucide's glyphs are vendored into a generated module,
so nothing is fetched from anywhere but this server. See [docs/web-client.md](docs/web-client.md).

Deployment is the `web` container — Caddy serving the download website at `/`, this client at `/app`,
and proxying everything else to the API. There is no installer and no update feed: a deploy is the new
bundle, and the next page load has it.

## The desktop client (frozen)

```bash
dotnet run --project src/Planner.Client
```

The Avalonia client still builds and still runs, and installed copies keep updating from the feed. It
is no longer where new work goes — the web client covers everything it does and rather more — but it is
kept in the tree because machines in the field are running it.
[docs/desktop-client.md](docs/desktop-client.md) is its reference.

To build a release of it:

```powershell
./build/release.ps1 -Version 1.1.0
```

That produces an installer, a delta package and a feed index in `./releases`, which compose mounts
into the API at `/updates`. Installed clients default to `https://planner.lyste.net/updates`
(overridable through `updateFeedUrl`) and check at startup and every four hours, download
what they find, and offer a restart — whether or not anyone has signed in.
Stable is the default channel; `-Channel win-beta` publishes a pilot build to the same feed.
See [docs/releasing.md](docs/releasing.md).

## Layout

```
src/
  Planner.Domain          entities and roles
  Planner.Contracts       DTOs, enums and the SignalR interface — no dependencies, shared by both ends
  Planner.Infrastructure  DbContext, EF configurations, migrations, seeding
  Planner.Api             minimal API endpoints, authorization, OpenIddict, the hub, the update feed
  Planner.Client          Avalonia desktop client — frozen
  Planner.AppHost         Aspire app host — the development stack as one command
client/                   the web client: SvelteKit, static, served at /app
website/                  the Astro download homepage, served at /
deploy/web.Dockerfile     builds both of those into one Caddy image
deploy/Caddyfile          what belongs to the website, the client, and the API
build/release.ps1         packages and publishes a desktop client release
releases/                 the Velopack update feed (git-ignored contents)
docs/                     architecture, database, roles, API, realtime, client and release references
tools/planner.http        example requests
```

## Documentation

| Document | What it covers |
| --- | --- |
| [docs/deploy-coolify.md](docs/deploy-coolify.md) | The VPS deployment: Coolify resources, image builds, release publishing, backups and recovery |
| [website/README.md](website/README.md) | Astro download homepage, live release list, and changelog authoring |
| [docs/architecture.md](docs/architecture.md) | Layering, the decisions worth knowing about, and why |
| [docs/database.md](docs/database.md) | Schema, relationships, indexes and the conventions behind them |
| [docs/roles-and-permissions.md](docs/roles-and-permissions.md) | Organisation roles, team roles, and the full permission matrix |
| [docs/api.md](docs/api.md) | Endpoint reference, filtering, paging, PATCH semantics, error shapes |
| [docs/realtime.md](docs/realtime.md) | Hub contract, group model, and a client sample |
| [docs/web-client.md](docs/web-client.md) | The web client: how it is served, the design system, the drag model, what is not built yet |
| [docs/desktop-client.md](docs/desktop-client.md) | The frozen Avalonia client: architecture, where it stores things |
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
| `Planner__Auth__TrustedProxyHops` | `0` | Reverse proxies in front of the API. Needed for per-client rate limiting; `2` on the Coolify VPS. |
| `Planner__Auth__AllowedOrigins__0` | — | CORS origins, one per index. Needed by neither client: the desktop one is not subject to CORS, and the web one is served from this API's own origin. Set it only for a browser application hosted somewhere else. |
| `Attachments__Path` | `App_Data/attachments` under the content root | Where uploaded attachment bytes live. **Set this to an absolute, persistent directory in any container**: the default is inside the application folder, which the image's non-root user cannot write to. Back it up with the database. |
| `Planner__Seed__OwnerEmail` | `owner@planner.local` | Bootstrap owner account. |
| `Planner__Seed__OwnerPassword` | — | Set it, or no owner is created. Minimum 12 characters. |
| `Planner__Seed__SeedDemoData` | `false` | Populate an empty database with sample content. |
| `Planner__Updates__Enabled` | `true` | Serve the desktop client's update feed. |
| `Planner__Updates__Directory` | `/var/lib/planner/updates` | Where release files live. Bound to `PLANNER_UPDATE_DIR` on the host. |
| `Planner__Updates__RequestPath` | `/updates` | Public path of the feed. Anonymous, deliberately. |

## Operational notes

- **Back up three things**: the Postgres volume, the keys volume and the attachments volume. Losing the
  keys volume invalidates every issued token; users simply sign in again, but it is avoidable. Losing
  the attachments volume leaves rows pointing at files that no longer exist.
- **TLS** is expected to terminate at a reverse proxy. The container speaks HTTP on 8080.
- **Migrations** ship inside the image. `AutoMigrate=false` plus
  `dotnet ef migrations script --idempotent` gives you a script to hand to a DBA instead.
- **Deleting** is deliberately rare. Teams, projects, issues and documents archive; users deactivate.
  Hard deletes exist but need team-lead or admin authority.
- **The update feed is anonymous** and served from `PLANNER_UPDATE_DIR`. That is intentional: a client
  must be able to fetch a fix for a release that broke sign-in. Keep old packages — clients that have
  been offline need the ones in between.
