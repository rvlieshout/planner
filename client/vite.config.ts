import { sveltekit } from '@sveltejs/kit/vite';
import { defineConfig } from 'vite';
import { sha, version } from './build-info.js';

// In development the API is a separate origin; in production it is the same one, because Caddy serves
// this app and proxies everything else to the API container. The proxy below erases that difference,
// so application code only ever writes same-origin paths like `/api/v1/issues`.
const api = process.env.PLANNER_SERVER_URL ?? 'http://localhost:8080';

// Set by the app host when Planner is reached through another address, such as a tunnel for testing an
// MCP client that cannot see localhost. Vite refuses requests for hosts it does not know.
const publicHost = process.env.PLANNER_PUBLIC_URL ? new URL(process.env.PLANNER_PUBLIC_URL).hostname : null;

export default defineConfig({
  plugins: [sveltekit()],

  // One version number, in package.json, reaching the status bar without a second place to bump.
  define: {
    __APP_VERSION__: JSON.stringify(version),
    __BUILD_SHA__: JSON.stringify(sha)
  },

  server: {
    port: 5175,
    strictPort: true,
    allowedHosts: publicHost ? [publicHost] : undefined,
    proxy: {
      '/api': { target: api, changeOrigin: false },
      '/connect': { target: api, changeOrigin: false },
      '/hubs': { target: api, changeOrigin: false, ws: true },
      // What an MCP client talks to: the endpoint itself and the OAuth discovery documents that tell it
      // where to sign in. In production Caddy passes both to the API already.
      '/mcp': { target: api, changeOrigin: false },
      '/.well-known': { target: api, changeOrigin: false }
    }
  }
});
