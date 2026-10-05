# Planner homepage

A static homepage for the demo VPS: `index.html`, `changelog.json` and `favicon.svg`, served by Caddy
from its own image as they are. There is nothing to install and nothing to build.

## Develop

Serve this folder with any static file server and open it:

```powershell
cd website
python -m http.server 4321
```

The page fetches `changelog.json` from beside itself and nothing else, so no API needs to be running
to work on it — but it does need a server, because a page opened from disk cannot fetch. No production
domain or private credentials are baked into the page.

## Changelog

Release notes live in `changelog.json` and are drawn by the page's own script, newest version first.
There is one entry per git tag (`git tag -l`); when you tag a release, add its entry from the commits
since the previous tag (`git log --format=%s vPREV..vNEW`), written for users and leaving out
build and deploy chores. Keep the page free of any product or company name. Use this shape:

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
`deploy/web.Dockerfile` builds SvelteKit in a Node stage and copies its output and these files next to
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
`deploy/Caddyfile` for the exact route allowlist: `/`, `/index.html`, `/favicon.svg` and `/changelog.json`
are static; everything else continues to the API.
