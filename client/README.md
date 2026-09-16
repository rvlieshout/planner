# Planner web client

SvelteKit 2 on Svelte 5, compiled to static files and served from the same origin as the API.

```bash
npm install
npm run dev          # http://localhost:5175/app, API proxied from http://localhost:8080
```

`PLANNER_SERVER_URL` points the development proxy somewhere else for one run. The Aspire app host sets
it automatically:

```bash
dotnet run --project src/Planner.AppHost      # database, API and this client, together
```

| Command | |
| --- | --- |
| `npm run dev` | Vite, with `/api`, `/connect` and `/hubs` proxied to the API |
| `npm run build` | Static output into `build/` |
| `npm run preview` | Serves that output |
| `npm run check` | `svelte-check` — the TypeScript and every binding in every component |
| `npm run icons` | Regenerates `src/lib/icons/icons.ts` from `lucide-static` |

Fonts and icons are compiled into the bundle, not fetched: no CDN, no font service, nothing outbound.

Everything else — the design system, the drag model, how the contract is kept in step with
`Planner.Contracts`, and what is not built yet — is in
[docs/web-client.md](../docs/web-client.md).
