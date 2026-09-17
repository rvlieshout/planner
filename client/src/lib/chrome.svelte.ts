import type { Snippet } from 'svelte';
import type { Command } from '$lib/commands.svelte';

/*
 * What the window frame is currently saying.
 *
 * The toolbar strip and the status bar live in the shell, above and below whatever page is open, so
 * they cannot bind through the content they are describing. A page publishes its heading, its actions
 * and its one-line summary here instead, and the shell reads them.
 *
 * `refresh` and `unsavedWork` are registered the same way and for the same reason: F5 and the
 * navigation guard are the shell's keyboard and the shell's router, but only the page knows what
 * reloading means and what would be lost by leaving.
 *
 * `commands` is the page's half of the command palette and the keyboard: what this screen can do on
 * top of what the shell always can.
 */

export interface PageChrome {
  title: string;
  subtitle?: string;
  /** The status bar's left-hand text: "11 issues in 6 columns". */
  status?: string;
  /** Buttons for the toolbar strip, as a snippet the shell renders in its own row. */
  actions?: Snippet;
  /** Offered in the command palette, under the page's title, and bound to their shortcuts. */
  commands?: Command[];
}

class Chrome {
  title = $state('');
  subtitle = $state<string | undefined>(undefined);
  status = $state<string | undefined>(undefined);
  actions = $state<Snippet | undefined>(undefined);
  commands = $state<Command[]>([]);

  /** What F5 and the toolbar's Refresh do on the page that is open. */
  refresh = $state<(() => void | Promise<void>) | null>(null);

  /**
   * Set by a page that can be dirty. Returns the summary of what would be lost, in the words the
   * prompt will use, or null when there is nothing to lose. A board or a list never sets it and is
   * therefore never interrupted.
   */
  unsavedWork = $state<(() => string | null) | null>(null);

  busy = $state(false);

  set(chrome: PageChrome): void {
    this.title = chrome.title;
    this.subtitle = chrome.subtitle;
    this.status = chrome.status;
    this.actions = chrome.actions;
    this.commands = chrome.commands ?? [];
  }

  /** Called by a page as it unmounts, so the previous page's heading never outlives it. */
  clear(): void {
    this.title = '';
    this.subtitle = undefined;
    this.status = undefined;
    this.actions = undefined;
    this.commands = [];
    this.refresh = null;
    this.unsavedWork = null;
    this.busy = false;
  }
}

export const chrome = new Chrome();
