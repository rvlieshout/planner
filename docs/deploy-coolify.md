# Deploying Planner on a Coolify VPS

The server runs [Coolify](https://coolify.io), which owns the reverse proxy, the TLS certificates,
the database and its backups. Planner supplies two images, built by GitHub Actions and pulled from
GHCR: the API, and a Caddy container holding the Astro site, the web client, and the routing
rules that decide which requests belong to which. Nothing is compiled on the VPS.

```text
Internet -> Coolify proxy (TLS) -> web:80 -> /            Astro homepage
                                          -> /app/*      the web client (static)
                                          -> everything   api:8080 -> Coolify PostgreSQL
```

One domain, `planner.lyste.net`, serves all of it: the homepage, the web client at `/app`, the API it
signs in to and the SignalR hub. The web client being on that same origin is deliberate — it is why
no CORS configuration exists anywhere in this deployment.

| Piece | Where it is defined |
| --- | --- |
| The stack Coolify runs | [`docker-compose.coolify.yml`](../docker-compose.coolify.yml) |
| Website / client / API routing | [`deploy/Caddyfile`](../deploy/Caddyfile), baked into the web image |
| The web image itself | [`deploy/web.Dockerfile`](../deploy/web.Dockerfile) — builds the website and the client, copies both into Caddy |
| Image build and deploy trigger | [`.github/workflows/release-images.yml`](../.github/workflows/release-images.yml) |

## 1. Prepare the server

Coolify is already installed. Planner needs one directory on the host and, because the GHCR
packages are private, a registry login for the Docker daemon that pulls them:

```bash
install -d -m 0700 /srv/planner/backups
echo "$GHCR_TOKEN" | docker login ghcr.io -u YOUR_GITHUB_USER --password-stdin
```

`GHCR_TOKEN` is a GitHub personal access token with `read:packages` only. Keep ports 80 and 443
open to the world for Coolify's proxy, and 22 restricted to your own address. Point a DNS **A** record for `planner.lyste.net` at the VPS before deploying, so a
certificate can be issued.

## 2. Push the repository to GitHub

A private repository, with the Coolify GitHub App installed on it so Coolify can read the compose
file. The image workflow itself only needs `GITHUB_TOKEN`, which Actions provides.

```powershell
git remote add origin git@github.com:YOUR_GITHUB_USER/planner.git
git push -u origin main
```

The first push to `main` runs [CI](../.github/workflows/ci.yml) and builds
`ghcr.io/YOUR_GITHUB_USER/planner-api` and `.../planner-web`, tagged `latest` and with the commit
SHA. The packages are private, which is right: the VPS authenticates with the token from step 1.

## 3. Create the PostgreSQL resource

In Coolify: **New Resource -> Database -> PostgreSQL 18**. Set a strong password, then enable
**scheduled backups** to an S3-compatible destination. This is the reason the database is a Coolify
resource rather than a service in the compose file: backups, retention and restore come with it.

Note the container name Coolify assigns. That is the hostname other containers reach it by.

## 4. Create the Planner stack

**New Resource -> Docker Compose**, pointed at this repository and `docker-compose.coolify.yml`.
Then, before the first deploy:

- Turn on **Connect to predefined network**, so the stack can reach the database container.
- Set the environment variables below.
- Open the `web` service's **Domains** field and replace the generated domain with
  `https://planner.lyste.net`.

| Variable | Value |
| --- | --- |
| `PLANNER_API_IMAGE` | `ghcr.io/YOUR_GITHUB_USER/planner-api:latest` |
| `PLANNER_WEB_IMAGE` | `ghcr.io/YOUR_GITHUB_USER/planner-web:latest` |
| `PLANNER_DB_HOST` | The database container name from step 3 |
| `PLANNER_DB_NAME`, `PLANNER_DB_USER`, `PLANNER_DB_PASSWORD` | From the database resource |
| `PLANNER_KEY_PASSWORD` | `openssl rand -hex 24`. Never changes; see below. |
| `PLANNER_OWNER_EMAIL` | The first account that can sign in |
| `PLANNER_OWNER_PASSWORD` | `openssl rand -hex 24`, at least 12 characters |
| `PLANNER_OWNER_NAME` | Optional display name |
| `PLANNER_SEED_DEMO` | `true` only on a throwaway deployment, and only before the first start |

Store the generated values in a password manager. `PLANNER_KEY_PASSWORD` protects the token signing
certificates on the `planner-keys` volume: change it later and those certificates cannot be read,
which signs out every client and invalidates every refresh token. It is not a rotatable secret.

Deploy. The API applies its EF migrations at startup, registers the `planner-desktop` OpenIddict
client and creates the owner account, so there is no SQL to import.

```bash
curl --fail https://planner.lyste.net/health/ready
curl --fail --silent https://planner.lyste.net/ | head -5
```

Expected: `Healthy`, a valid certificate, and the homepage.

## 5. Deploying a new backend version

Push to `main`. Actions builds both images and, when `COOLIFY_WEBHOOK_URL` and `COOLIFY_TOKEN` are
set as repository secrets, calls Coolify's deploy webhook; without them, press **Deploy** in
Coolify. Both images are pushed before the webhook fires, so a failed build never half-updates the
stack.

Copy the webhook URL from the resource's **Webhooks** tab, and create the token under
**Keys & Tokens -> API tokens**.

Migrations run at startup, so rolling back is not simply redeploying yesterday's image. When the
schema is unchanged, set `PLANNER_API_IMAGE` to a commit-SHA tag and redeploy. When the schema did
change, restore the pre-upgrade database alongside the matching image and accept the loss of writes
made since. Ship a backwards-compatible API before the client that needs it.

## 6. Backups

Three things have to survive the server, and Coolify covers only the first.

1. **The database.** Coolify's scheduled backups, from step 3. Verify that one actually restores.
2. **The keys volume.** `planner-keys` holds the token signing certificates and the data-protection
   keys. Losing it signs everyone out permanently.
3. **The attachments volume.** `planner-attachments` holds the bytes of every uploaded file. The rows
   describing them are in the database, so losing this volume leaves the tracker pointing at files
   that no longer exist — back it up *with* the database, on the same schedule.

For 2 and 3, a Coolify **Scheduled Task**, or from your PC:

```powershell
$stamp = (Get-Date).ToUniversalTime().ToString('yyyyMMddTHHmmssZ')
ssh root@planner.lyste.net "docker run --rm -v planner-keys:/keys:ro -v /srv/planner/backups:/out alpine:3 tar -czf /out/keys-$stamp.tar.gz -C /keys ."
ssh root@planner.lyste.net "docker run --rm -v planner-attachments:/files:ro -v /srv/planner/backups:/out alpine:3 tar -czf /out/attachments-$stamp.tar.gz -C /files ."
scp root@planner.lyste.net:/srv/planner/backups/keys-$stamp.tar.gz .
scp root@planner.lyste.net:/srv/planner/backups/attachments-$stamp.tar.gz .
```

Coolify prefixes volume names with the resource's identifier, so check the name it shows for the
stack rather than assuming `planner-keys`. These archives contain signing keys: store them
privately.

## 7. Restoring onto a new server

1. Install Coolify, then follow steps 1 to 4 with the **same** `PLANNER_KEY_PASSWORD`.
2. Deploy once so the volumes exist, then stop the stack.
3. Restore the database through Coolify's backup UI, into an empty database.
4. Restore the keys archive into the keys volume:

   ```bash
   docker run --rm --user 0 -v planner-keys:/keys -v /srv/planner/restore:/backup:ro \
       alpine:3 tar -xzf /backup/keys-TIMESTAMP.tar.gz -C /keys
   ```

   `--user 0`, with an archive that preserved numeric ownership, is what lets the non-root API user
   read them afterwards.
5. Start the stack, and verify readiness, sign-in, existing issues and realtime updates before
   moving DNS.

Clients need no reconfiguration if the domain is unchanged. Changing owner or database passwords in
the environment does not reset credentials that already exist in a populated database.

## Notes and current limits

- **Forwarded headers.** The chain is Coolify's proxy, then this stack's web container, so
  `Planner__Auth__TrustedProxyHops` is 2. Without it the sign-in rate limiter sees a single address
  for the entire internet. Another proxy in front means raising the number to match.
- **`AllowInsecureHttp` stays true.** TLS terminates at Coolify; inside the container network the
  API speaks plain HTTP. With forwarded headers read, it still knows the public request was HTTPS.
- **Attachments are files on a volume.** `Attachments__Path` points at `planner-attachments`, and the
  rows in the database point at files that exist only there — so it is backed up with the database,
  not instead of it. Attachments added as links rather than uploads store metadata only.
- **This is a single server.** Deploys and restores mean brief downtime; clients reconnect on their
  own.
