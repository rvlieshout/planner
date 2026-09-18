# Planner homepage

A static Astro homepage for the demo VPS. Caddy serves the generated files from its own image;
there is no Node process running on the VPS after the image is built.

## Develop

Use Node.js 24 LTS (matching the Docker build), or Node 22.19 or later.

```powershell
cd website
npm ci
npm run dev
```

Open the local address printed by Astro. The page is entirely static — it fetches nothing at runtime —
so no API needs to be running to work on it. No production domain or private credentials are baked
into the page.

```powershell
npm run build
npm run preview
```

`dist/` is the production output. The preview does not represent Caddy's API routes.

## Changelog

Release notes live in `src/data/changelog.json` and are rendered at build time, newest version first.
The file starts empty. Use this shape:

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

`date` is optional; use `YYYY-MM-DD` when known. An entry with no `changes` still appears, labeled as
having no release notes. The page renders note text as text, not HTML. Publishing a note is a deploy:
there is no feed to cross-reference any more, so this file is the whole source of truth. There are no
external fonts, analytics or CDN assets. The demo page requests `noindex`; this is not an access
restriction.

## Deploy

The site ships inside the `planner-web` image, which it shares with the web client:
`deploy/web.Dockerfile` builds Astro and SvelteKit in two Node stages and copies both outputs next to
`deploy/Caddyfile` in a Caddy image — the site at `/srv/site`, the client at `/srv/app`. Pushing to
`main` builds and publishes it, and Coolify redeploys — see
[the deployment guide](../docs/deploy-coolify.md). Changing the site or its notes needs nothing else.

The hero leads with **Open Planner**, which is the browser client at `/app`. There is nothing to
download: the Windows desktop client and its update feed have both been removed.

```bash
curl --fail https://planner.lyste.net/
```

Roll back by pinning `PLANNER_WEB_IMAGE` to an earlier commit-SHA tag. Redeploying the web container
briefly interrupts connections, including realtime clients, which reconnect on their own. See
`deploy/Caddyfile` for the exact route allowlist: `/`, `/index.html`, `/favicon.svg` and `/_astro/*`
are static; everything else continues to the API.
