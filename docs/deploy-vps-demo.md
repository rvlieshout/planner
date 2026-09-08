# Deploying a Planner demo on a small VPS

Use one Ubuntu 26.04 LTS VPS running PostgreSQL 18, the Planner API, and Caddy for automatic HTTPS.
The API already hosts the Windows installer and Velopack update feed. An Astro homepage provides
the download button, connection instructions and changelog; its static files are built into the
Caddy image, so no extra running service, object storage, or managed database is needed. This is a single-server demo with brief
downtime during maintenance, not a highly available deployment.

## 1. Choose the server and domain

Start with **2 shared x86-64 vCPUs, 4 GB RAM, and 40 GB disk**, Ubuntu 26.04 LTS, in a European
location. This is a sizing estimate for a handful of demo users, not a measured capacity limit.
Hetzner's CX23 has these specifications; availability varies, so choose an available x86 plan
with at least these resources. Check the checkout total for VAT, IPv4 and backups rather than
relying on an old quoted price. See [Hetzner's current plans](https://www.hetzner.com/cloud/cost-optimized/).

Use a subdomain you control, for example `planner-demo.example.com`. Throughout this guide,
replace that name and `VPS_IP` with your actual domain and public IPv4 address. The client remains
a Windows x64 application even though its server runs Linux.

Create the VPS with your SSH public key. Attach a Hetzner Cloud Firewall allowing inbound:

| Port | Source | Purpose |
| --- | --- | --- |
| TCP 22 | Your administrator public IP only | SSH and file uploads |
| TCP 80 | Any IPv4/IPv6 | HTTP redirect and certificate issuance |
| TCP 443 | Any IPv4/IPv6 | API, realtime, installer and updates |

Keep outbound access allowed for DNS, image downloads and certificate issuance. No inbound
5432, 8080 or 5050 rule is needed. The supplied configuration publishes neither the database nor
the API container port. Do not rely on UFW alone to restrict Docker-published ports; see
[Docker's Ubuntu firewall notes](https://docs.docker.com/engine/install/ubuntu/).

Create a DNS **A** record for the subdomain pointing to the VPS. Only create an AAAA record if
IPv6 is configured and reachable too. Use ordinary DNS without a CDN proxy for the initial demo.
Caddy obtains and renews the certificate once DNS and ports are reachable:
[automatic HTTPS requirements](https://caddyserver.com/docs/automatic-https).

## 2. Prepare Ubuntu

Commands in Linux blocks run in **Bash on the VPS**, as root (`ssh root@VPS_IP`). Windows blocks
run in **PowerShell on your development PC**, from this repository's root. Root is used here to
keep the one-person demo protocol simple; protect the SSH key and restrict port 22 as above.

```bash
apt-get update
apt-get upgrade -y
apt-get install -y ca-certificates curl git openssl
install -m 0755 -d /etc/apt/keyrings
curl -fsSL https://download.docker.com/linux/ubuntu/gpg -o /etc/apt/keyrings/docker.asc
chmod a+r /etc/apt/keyrings/docker.asc
cat > /etc/apt/sources.list.d/docker.sources <<EOF
Types: deb
URIs: https://download.docker.com/linux/ubuntu
Suites: resolute
Components: stable
Architectures: amd64
Signed-By: /etc/apt/keyrings/docker.asc
EOF
apt-get update
apt-get install -y docker-ce docker-ce-cli containerd.io docker-buildx-plugin docker-compose-plugin
systemctl enable --now docker
docker compose version
install -d -m 0755 /opt/planner /srv/planner/releases
install -d -m 0700 /srv/planner/incoming /srv/planner/backups
```

These installation commands target Ubuntu 26.04 (Resolute) **amd64** specifically, following
[Docker's official repository installation](https://docs.docker.com/engine/install/ubuntu/).
Reboot first if the OS upgrade requires it, then reconnect.

## 3. Copy the source and configure the deployment

Use a reviewed, committed revision. This exports tracked files only; commit any intended local
changes first. It deliberately excludes local secrets and git-ignored installer output.

```powershell
git rev-parse HEAD
git archive --format=tar --output=planner-source.tar HEAD
scp ./planner-source.tar root@VPS_IP:/opt/planner/
```

The revision must include `docker-compose.demo.yml`, `deploy/Caddyfile`, and the `website/`
source directory including `package-lock.json`.
On the VPS:

```bash
cd /opt/planner
tar -xf planner-source.tar
umask 077
cat > .env <<EOF
COMPOSE_FILE=docker-compose.demo.yml
PLANNER_DOMAIN=planner-demo.example.com
PLANNER_API_TAG=demo-1
PLANNER_WEBSITE_TAG=demo-1
POSTGRES_PASSWORD=$(openssl rand -hex 24)
PLANNER_KEY_PASSWORD=$(openssl rand -hex 24)
PLANNER_OWNER_EMAIL=owner@example.com
PLANNER_OWNER_PASSWORD=$(openssl rand -hex 24)
PLANNER_SEED_DEMO=false
EOF
chmod 600 .env
nano .env
```

Edit the domain and owner email. Save the generated passwords in your password manager; the
owner password is the first login credential. Hex passwords avoid connection-string and
Compose interpolation issues. Never commit or put `.env` in the downloads directory.

For sample teams/issues, set `PLANNER_SEED_DEMO=true` **before first start**. The seeder also
creates `dana@planner.local` and `sam@planner.local` with the same initial owner password.
Change their passwords or deactivate them through Users & access before sharing the demo.
Turning seeding off later does not remove those accounts or sample records. Use synthetic
demo data; attachments currently store links to external files, not uploaded file contents.

`COMPOSE_FILE` selects the standalone demo stack. **Do not combine it with the normal
`docker-compose.yml`**, whose development-friendly port mappings expose additional services.
All subsequent `docker compose` commands run from `/opt/planner` so they load this `.env`.

## 4. Start and verify the service

These steps assume a fresh database volume. Both Compose configurations use `postgres:18-alpine`
to match development. PostgreSQL 18's official image uses `/var/lib/postgresql/18/docker` for
`PGDATA`; the volume therefore mounts at `/var/lib/postgresql`, not the older
`/var/lib/postgresql/data` location. See the [official image's volume guidance](https://hub.docker.com/_/postgres).
If you already started the earlier PostgreSQL 17 configuration, follow the major-version
migration instructions in section 7 before starting this configuration against existing data.

```bash
cd /opt/planner
docker compose config --quiet
docker compose build api caddy
docker compose up -d
docker compose ps
docker compose logs --tail=100 api caddy
curl --fail https://planner-demo.example.com/health/ready
```

Expected: `Healthy`, database and API containers healthy, and a valid public TLS certificate.
The API automatically applies migrations, registers `planner-desktop`, and creates the initial
owner. No manual SQL import is needed. Initial image building may take several minutes; if it
runs out of memory, resize to 8 GB or build the Linux amd64 image on another machine and transfer
it with `docker save` / `docker load`.

The resulting locations are:

| What | URL / host location |
| --- | --- |
| Download homepage and changelog | `https://planner-demo.example.com/` |
| Client server address | `https://planner-demo.example.com` |
| Windows installer | `https://planner-demo.example.com/updates/Planner-win-Setup.exe` |
| Update feed base | `https://planner-demo.example.com/updates` |
| Feed index | `https://planner-demo.example.com/updates/releases.win.json` |
| API reference | `https://planner-demo.example.com/scalar` |
| Ready check | `https://planner-demo.example.com/health/ready` |
| Published installer/packages on disk | `/srv/planner/releases/` |
| Database volume | `planner-demo_pgdata` |
| Signing and data-protection keys | `planner-demo_keys` |

The installer/index will return 404 until you publish the first client release. Share the homepage
with demo users; it includes the installer link and the server address to enter in the desktop
client. This site is a download page, not a browser version of Planner. Caddy proxies WebSockets for
SignalR automatically ([reverse proxy documentation](https://caddyserver.com/docs/caddyfile/directives/reverse_proxy)).

The current API does not process forwarded headers. This configuration retains the existing
`AllowInsecureHttp=true` mode on the private container network; public traffic uses HTTPS.
Its login limiter sees the proxy address, so demo users share the limit of 20 token requests
per minute. Before a wider rollout, configure trusted-proxy forwarded headers and verify the
external HTTPS issuer and per-user-IP limiting. Do not expose API port 8080 as a workaround.

## 5. Build and publish the Windows client

On your Windows build PC, install the .NET 10 SDK and use the repository's pinned Velopack tool
through the existing script. Choose a version higher than every version already distributed:

```powershell
./build/release.ps1 -Version 1.1.0
ssh root@VPS_IP "mkdir -p /srv/planner/incoming/1.1.0"
scp ./releases/*.nupkg ./releases/*.exe ./releases/*.zip ./releases/releases.win.json root@VPS_IP:/srv/planner/incoming/1.1.0/
```

Keep the local `releases` folder between builds so Velopack can produce deltas. The first release
can contain only a full package. If you change build PCs, retrieve the existing feed first.
Do not publish directly into the live directory with `-PublishTo` over a network share: the index
could arrive before the package has finished uploading.

After `scp` succeeds, publish on the VPS. Packages go live first, the installer is renamed
atomically, and the index goes live last. The incoming and release directories share `/srv/planner`
on the same filesystem, making these renames atomic. Do not run two publications at once.

```bash
set -e
stage=/srv/planner/incoming/1.1.0
live=/srv/planner/releases
test -s "$stage/releases.win.json"
test -s "$stage/Planner-win-Setup.exe"
chmod 644 "$stage"/*
for file in "$stage"/*.nupkg; do
    test -s "$file"
    name=$(basename "$file")
    if [ -e "$live/$name" ]; then
        cmp --silent "$file" "$live/$name"
    else
        mv "$file" "$live/$name"
    fi
done
for file in "$stage"/*.exe "$stage"/*.zip; do
    test -f "$file" || continue
    mv -f "$file" "$live/$(basename "$file")"
done
mv -f "$stage/releases.win.json" "$live/releases.win.json"
curl --fail https://planner-demo.example.com/updates/releases.win.json
curl --fail --head https://planner-demo.example.com/updates/Planner-win-Setup.exe
```

Stop if any command fails; do not advance the index after an incomplete package transfer.
If an existing version's package differs, rebuild under a new higher version. Retain old
packages. No container restart is needed. The feed is anonymous by design, and the Caddy
configuration disables caching for it. Only publish release artifacts in that directory.

Install using `Planner-win-Setup.exe` on a Windows x64 PC. Builds are currently unsigned, so
Windows may show an unknown-publisher/SmartScreen prompt. For wider distribution, configure
code signing as described in [releasing.md](releasing.md); HTTPS does not sign the executable.

On the login screen enter `https://planner-demo.example.com` as the server and sign in with the
owner credentials. The installer does **not** embed your server address. Once set, the client
remembers it and derives `/updates` automatically. Create individual demo users in Users & access.

Verify an issue can be created and appears in a second signed-in client through the realtime
connection. Then publish `1.1.1` using the same procedure (replace `1.1.0` in all staging paths),
restart an installed `1.1.0` client, and verify the update banner and restart/install flow.
Installed clients check on startup and every four hours. Use the installer for this test;
an unpacked development build does not exercise installed-app updates.

### Publish changelog notes

Add the real changes to `website/src/data/changelog.json` using the example in
[website/README.md](../website/README.md), then commit and transfer the new source. Do not invent
changes for historical versions. The website lists stable full releases directly from the live
feed, even when no authored notes exist, and only shows notes for published versions.

For site or changelog changes, set a new `PLANNER_WEBSITE_TAG` in the VPS `.env`, then:

```bash
cd /opt/planner
docker compose build caddy
docker compose up -d --no-deps caddy
curl --fail https://planner-demo.example.com/
```

You can deploy notes before publishing the matching client feed. Uploading packages alone needs
no website rebuild. Recreating Caddy briefly interrupts connections; existing clients reconnect.
Verify the homepage, copy-server button, installer link, changelog and `/health/ready` after
the first deployment. Keep the previous website tag for rollback with `--no-build`.

## 6. Back up before a demo and before every server upgrade

Take a consistent database dump and key archive while the API is stopped. This briefly interrupts
users and downloads. On the VPS:

```bash
set -e
cd /opt/planner
umask 077
backup=/srv/planner/backups/$(date -u +%Y%m%dT%H%M%SZ)
mkdir -p "$backup"
docker compose stop api
docker compose exec -T db pg_dump -U planner -d planner -Fc > "$backup/planner.dump"
docker run --rm -v planner-demo_keys:/keys:ro -v "$backup:/backup" alpine:3 \
    tar -czf /backup/keys.tar.gz -C /keys .
cp .env "$backup/deployment.env"
docker compose images > "$backup/images.txt"
docker compose start api
docker compose exec -T db pg_restore --list < "$backup/planner.dump" > /dev/null
tar -tzf "$backup/keys.tar.gz" > /dev/null
```

If a step fails, diagnose it and run `docker compose start api` to restore service; do not treat
the partial backup as valid. Also retain the exact source revision/image and release artifacts.
Copy the timestamped backup folder to a separate machine or encrypted backup destination:

```powershell
scp -r root@VPS_IP:/srv/planner/backups/TIMESTAMP ./planner-backup-TIMESTAMP
```

Replace `TIMESTAMP` with the folder created above. These backups include credentials and keys;
store them privately. For a continuing demo, run this daily in a maintenance window and retain
seven daily off-server copies. VPS snapshots are useful additional recovery points, but do not
replace a database dump and a tested restore. Keep an eye on disk space: old release packages
and Docker build caches accumulate on a 40 GB disk.

## 7. Upgrade or recover

**Existing PostgreSQL 17 deployment:** do not reuse its physical data directory with the
PostgreSQL 18 image. Before replacing the old Compose configuration, stop the API and take the
database dump and key backup from section 6 while the PostgreSQL 17 database is still running.
Leave the API stopped so the source does not acquire writes after the dump. Keep that old stack
and volume intact for rollback. Provision a fresh VPS with this PostgreSQL 18 configuration,
then use the empty-database restore procedure below to import the dump and restore the keys.
Use the matching API revision initially, verify login and existing records, and only then switch
DNS. Keep the old server stopped after cutover. EF migrations update the application schema;
they do not upgrade the PostgreSQL server's storage format. A fresh demo needs no major-version
migration, and ordinary backups/restores now run with the PostgreSQL 18 container tools.

**Server upgrade:** back up first, retain the current API image tag and source archive, export the
new committed source as in step 3, and extract it into `/opt/planner`. Keep `.env` and the volumes.
Set a new `PLANNER_API_TAG` in `.env` (for example `demo-2`), then:

```bash
cd /opt/planner
docker compose build api
docker compose up -d --no-deps api
docker compose logs --tail=100 api
curl --fail https://planner-demo.example.com/health/ready
```

Deploy a backwards-compatible API before publishing a client that needs it. Migrations run at
startup, so rollback is not necessarily just selecting the previous image. If the schema remains
compatible, set the previous tag and use `docker compose up -d --no-deps --no-build api`.
Otherwise restore the pre-upgrade database with its matching image; this loses writes made
after that backup. For a bad client build, ship the corrected code with a **higher** version.

**Restore drill / lost VPS:** use a fresh VPS or isolated deployment with empty volumes. Install
Docker, restore the matching source and `.env` to `/opt/planner` (mode 600), and restore the release
folder. Point DNS at the recovery host only when ready. Copy a backup to `/srv/planner/restore`.
Use the original `PLANNER_KEY_PASSWORD`; changing it cannot decrypt existing certificates.

```bash
set -e
cd /opt/planner
docker compose build api caddy
docker compose up -d db --wait
docker compose create api
docker compose exec -T db pg_restore -U planner -d planner --no-owner --exit-on-error \
    < /srv/planner/restore/planner.dump
docker run --rm --user 0 -v planner-demo_keys:/keys -v /srv/planner/restore:/backup:ro alpine:3 \
    tar -xzf /backup/keys.tar.gz -C /keys
docker compose up -d
```

This restore command assumes an **empty** database, not a running migrated copy. The key archive
preserves numeric ownership for the non-root API user. Caddy obtains fresh HTTPS certificates;
its own TLS volume need not be restored. Verify readiness, login, existing issues, realtime and
client downloads before declaring the restore successful. If retaining the old domain, clients
need no server-address change. Changing `.env` owner/database passwords alone does not reset
existing credentials in a populated database.

For routine inspection use `docker compose logs --tail=100`, `docker stats --no-stream`,
`df -h`, and `docker system df`. Never run `docker compose down -v` against data you want to keep.
To pause the demo use `docker compose stop`; this keeps data but the VPS continues to be billed.
When finished, verify an off-server backup before deleting the VPS and removing its DNS record.

## Validation status

This protocol is based on the repository's Dockerfile, seeding, authentication, update-serving and
release code. The standalone Compose model passed `docker compose -f docker-compose.demo.yml
config --quiet` with placeholder environment values, and `git diff --check` passed.
The Astro production build passed with Node 24. Docker was not running on the development
machine, so the combined website image was not built or run there.
An actual VPS deployment, certificate issuance, restore drill and Windows update round trip must
still be verified using the steps above before presenting the demo.
