import { sveltekit } from '@sveltejs/kit/vite';
import { defineConfig } from 'vite';
import { execFileSync } from 'node:child_process';
import { readFileSync } from 'node:fs';

const { version } = JSON.parse(readFileSync(new URL('./package.json', import.meta.url), 'utf8'));

// The commit this bundle was built from. CI passes it in, because the image build has no .git to
// ask; a local build falls back to the checkout's HEAD, and anything else says so rather than
// claiming a commit it cannot name.
function buildSha() {
  const fromEnv = process.env.PLANNER_BUILD_SHA?.trim();
  if (fromEnv) return fromEnv;

  try {
    return execFileSync('git', ['rev-parse', 'HEAD'], { encoding: 'utf8', stdio: ['ignore', 'pipe', 'ignore'] }).trim();
  } catch {
    return 'unknown';
  }
}

// In development the API is a separate origin; in production it is the same one, because Caddy serves
// this app and proxies everything else to the API container. The proxy below erases that difference,
// so application code only ever writes same-origin paths like `/api/v1/issues`.
const api = process.env.PLANNER_SERVER_URL ?? 'http://localhost:8080';

export default defineConfig({
  plugins: [sveltekit()],

  // One version number, in package.json, reaching the status bar without a second place to bump.
  define: { __APP_VERSION__: JSON.stringify(version), __BUILD_SHA__: JSON.stringify(buildSha()) },

  server: {
    port: 5175,
    strictPort: true,
    proxy: {
      '/api': { target: api, changeOrigin: true },
      '/connect': { target: api, changeOrigin: true },
      '/hubs': { target: api, changeOrigin: true, ws: true }
    }
  }
});
