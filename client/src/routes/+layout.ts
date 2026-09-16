/*
 * The whole application is one static shell, routed in the browser.
 *
 * There is no Node runtime in the deployment — Caddy serves these files and proxies everything else
 * to the API container — so nothing is rendered on a server and nothing is prerendered ahead of time.
 * The adapter writes a single index.html that answers every path under /app, and this file is what
 * tells SvelteKit that is the intention rather than an oversight.
 */
export const ssr = false;
export const prerender = false;
export const trailingSlash = 'never';
