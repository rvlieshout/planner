# Planner

An on-premises project and issue tracker in the shape of Linear: teams own projects, projects run to
milestones, and work happens as issues on a board. Everything lives on your own hardware — Postgres
for storage, a single .NET container for the API, no outbound calls.

This repository is the **backend**: the database design, the HTTP API and the realtime feed. The
Avalonia desktop client is a separate deliverable and consumes the DTOs in `Planner.Contracts`
directly.

## What is here

| Piece | Choice |
| --- | --- |
| Runtime | .NET 10, minimal APIs |
| Database | PostgreSQL 17, EF Core 10 (Npgsql) |
| Identity | ASP.NET Core Identity, users and roles in the same database |
| Tokens | OpenIddict 7 — self-hosted OAuth 2.0 / OIDC, password + refresh grants, plain JWTs |
| Realtime | SignalR hub at `/hubs/planner`, strongly typed against a shared interface |
| Docs | OpenAPI 3.1 at `/openapi/v1.json`, Scalar UI at `/scalar` |
| Packaging | Docker Compose: `db` + `api` (+ optional pgAdmin) |

71 HTTP operations across teams, members, workflow states, labels, projects, milestones, documents,
issues, sub-issues, relations, comments, attachments, users and the activity feed.

## Run it

Docker Desktop is the only prerequisite.

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

## Run it without Docker

```bash
docker compose up -d db                        # or point at your own Postgres
cd src/Planner.Api
dotnet user-secrets set "Planner:Seed:OwnerPassword" "SomethingLong123!"
dotnet run
```

`appsettings.Development.json` targets `localhost:5432`, keeps certificates in `./keys`, allows plain
HTTP and seeds demo data.

## Layout

```
src/
  Planner.Domain          entities, enums, roles — no framework beyond Identity's base classes
  Planner.Contracts       request/response DTOs + the SignalR interface, shared with the client
  Planner.Infrastructure  DbContext, EF configurations, migrations, seeding
  Planner.Api             minimal API endpoints, authorization, OpenIddict, the hub
docs/                     architecture, database, roles, API and realtime references
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

## Operational notes

- **Back up two things**: the Postgres volume and the keys volume. Losing the keys volume invalidates
  every issued token; users simply sign in again, but it is avoidable.
- **TLS** is expected to terminate at a reverse proxy. The container speaks HTTP on 8080.
- **Migrations** ship inside the image. `AutoMigrate=false` plus
  `dotnet ef migrations script --idempotent` gives you a script to hand to a DBA instead.
- **Deleting** is deliberately rare. Teams, projects, issues and documents archive; users deactivate.
  Hard deletes exist but need team-lead or admin authority.
