import adapter from '@sveltejs/adapter-static';

/**
 * The client ships as static files inside the Caddy image that already serves the website, so it is
 * prerendered to a single shell and routed in the browser. `fallback` is what makes that work: every
 * path under /app that is not a real file is answered with the shell, which then reads the URL.
 *
 * `paths.base` is '/app' because that is where Caddy mounts it. SvelteKit rewrites every internal
 * link and asset URL with it, so nothing in the app has to know the prefix.
 *
 * @type {import('@sveltejs/kit').Config}
 */
export default {
  kit: {
    adapter: adapter({ fallback: 'index.html', precompress: true }),
    paths: { base: '/app', relative: false },
    alias: { $components: 'src/lib/components' },

    // No server runtime, so nothing is prerendered ahead of the shell and every route is resolved in
    // the browser against a live API.
    prerender: { entries: [] }
  }
};
