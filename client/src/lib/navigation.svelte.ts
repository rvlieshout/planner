import { beforeNavigate, goto } from '$app/navigation';
import { resolve } from '$app/paths';
import { chrome } from '$lib/chrome.svelte';
import { confirm } from '$components/confirm.svelte';
import type { Pathname } from '$app/types';

/*
 * Leaving a page with unsaved work.
 *
 * A modal cannot be navigated past. A page can, so the router asks first — and every route that
 * replaces the content pane goes through the same guard: a sidebar row, a menu item, the browser's
 * own back button, a keyboard shortcut, signing out.
 *
 * The one exception is closing the tab, where the browser's own "leave site?" prompt is the whole of
 * what a page is allowed to say.
 */

let bypass = false;

/** Installed once, by the application shell. */
export function installNavigationGuard(): void {
  beforeNavigate(async (navigation) => {
    if (bypass) {
      bypass = false;
      return;
    }

    // Leaving the application entirely is the browser's business, not this guard's.
    if (navigation.type === 'leave') return;

    const summary = chrome.unsavedWork?.();
    if (!summary) return;

    const target = navigation.to?.url;
    if (!target) return;

    // The answer is a promise and this hook is synchronous, so the navigation is stopped and then
    // repeated once the answer arrives. Repeating it is what makes the back button work too:
    // cancelling alone would leave the address bar one step ahead of the page.
    navigation.cancel();

    if (await confirm.discard(summary)) {
      chrome.unsavedWork = null;
      bypass = true;
      await goto(target.pathname + target.search + target.hash);
    }
  });

  // The tab itself. Nothing here decides the wording — every browser shows its own.
  window.addEventListener('beforeunload', (event) => {
    if (chrome.unsavedWork?.()) event.preventDefault();
  });
}

/**
 * Goes somewhere, asking first if the current page is dirty.
 *
 * For the places that are not a link: a command, a keyboard shortcut, or a page routing itself
 * onward after a save.
 */
export async function navigate(path: Pathname, options: { force?: boolean } = {}): Promise<boolean> {
  const summary = options.force ? null : chrome.unsavedWork?.();

  if (summary && !(await confirm.discard(summary))) return false;

  chrome.unsavedWork = null;
  bypass = true;
  await goto(resolve(path));
  return true;
}

/** Runs something only if the current page is willing to be left. */
export async function mayDiscard(): Promise<boolean> {
  const summary = chrome.unsavedWork?.();
  if (!summary) return true;

  const answer = await confirm.discard(summary);
  if (answer) chrome.unsavedWork = null;

  return answer;
}
