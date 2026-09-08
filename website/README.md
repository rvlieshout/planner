# Planner downloads

A static Astro homepage for the demo VPS. Caddy serves the generated files from its own image;
there is no Node process running on the VPS after the image is built.

## Develop

Use Node.js 24 LTS (matching the Docker build), or Node 22.19 or later.

```powershell
cd website
npm ci
npm run dev
```

Open the local address printed by Astro. `/updates` proxies to `http://localhost:8080`, so start
the local API and publish client packages to its mounted release folder to see release history.
If that API is absent, the page shows the feed-unavailable state. The connection address shown
locally is the preview origin; on the VPS it automatically becomes the actual public origin.
No production domain or private credentials are baked into the page.

```powershell
npm run build
npm run preview
```

`dist/` is the production output. The dev proxy is for `npm run dev`; the production preview
does not represent Caddy's API routes.

## Changelog

The browser reads `/updates/releases.win.json`, selects stable full Planner packages and sorts
versions numerically, newest first. Delta entries and prereleases do not duplicate the history.
The installer button always points to `/updates/Planner-win-Setup.exe`.

Edit `src/data/changelog.json` to add the actual notes for each release. The file starts empty
because the existing feed supplies versions but no description of what changed. Use this shape,
replacing the illustrative text with the real changes and actual release date:

```json
[
  {
    "version": "1.1.1",
    "title": "A short title for this release",
    "date": "2026-09-08",
    "changes": ["Describe a real change users will notice."]
  }
]
```

`date` is optional; use `YYYY-MM-DD` when known. Keep version strings identical to the feed.
Notes are displayed only for published versions, so you can deploy them ahead of the packages.
Published versions with no authored notes still appear, labeled as having no release notes.
The page renders note text as text, not HTML. There are no external fonts, analytics or CDN assets.
The demo page requests `noindex`; this is not an access restriction.

## Deploy

Follow [the VPS protocol](../docs/deploy-vps-demo.md). `docker compose build caddy` builds Astro
and copies its output into the Caddy image. After changing the site or its notes, use a new
`PLANNER_WEBSITE_TAG` in `/opt/planner/.env`, then run from `/opt/planner`:

```bash
docker compose build caddy
docker compose up -d --no-deps caddy
curl --fail https://planner-demo.example.com/
```

Keep the previous website image tag for rollback. Updating Caddy briefly interrupts connections,
including realtime clients, which reconnect. Client package uploads alone need no site rebuild
or container restart. See `deploy/Caddyfile` for the exact route allowlist: `/`, `/index.html`,
`/favicon.svg` and `/_astro/*` are static; everything else continues to the API.
