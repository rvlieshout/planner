import { updated } from '$app/state';
import { base } from '$app/paths';
import { mayDiscard } from '$lib/navigation.svelte';

/*
 * A newer build on the server.
 *
 * SvelteKit does the watching: svelte.config.js has it fetch `_app/version.json` every half hour, and
 * `updated.current` turns true once the answer differs from the build this tab is running. What is
 * left here is saying which version that is, and moving onto it.
 *
 * Moving onto it is a reload, because nothing else swaps a bundle. Two things keep that from being a
 * disruption: the next ordinary link click already becomes one (see navigation.svelte.ts), so most
 * people only ever notice the number in the status bar going up; and the button, for someone who
 * wants it now, reloads in place — same URL, same session — after asking about unsaved work.
 */
class AppUpdate {
  /** The version the server now has, or null while it is unknown or unchanged. */
  version = $state<string | null>(null);

  get available(): boolean {
    return updated.current;
  }

  /** Reads the new version's name. Called when `available` turns true; harmless to call again. */
  async describe(): Promise<void> {
    try {
      const response = await fetch(`${base}/_app/version.json`, {
        cache: 'no-cache'
      });
      if (!response.ok) return;

      const { version } = (await response.json()) as { version?: string };
      // The name is `<version>+<commit>`; only the version is worth putting in a status bar.
      this.version = version?.split('+')[0] || null;
    } catch {
      // The button still works without the number on it.
    }
  }

  async apply(): Promise<void> {
    if (await mayDiscard()) location.reload();
  }
}

export const appUpdate = new AppUpdate();
