import { defineConfig } from 'astro/config';

export default defineConfig({
  output: 'static',
  vite: {
    server: {
      // Local API only. Production routes are handled by deploy/Caddyfile.
      proxy: { '/updates': 'http://localhost:8080' },
    },
  },
});
