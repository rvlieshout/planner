import { notifications } from '$lib/api';
import type { Guid } from '$lib/api/types';
import { realtime } from '$lib/realtime/hub.svelte';

/*
 * The unread badge.
 *
 * The count is the server's, never worked out here: it is pushed to every connection of this user
 * whenever anything moves it — a new entry, a read in another tab, a "mark all read" — so the sidebar
 * shows the same number everywhere without this store holding a single notification. The Inbox page
 * holds the entries themselves.
 */
class InboxBadge {
  unread = $state(0);

  #wired = false;

  async initialize(): Promise<void> {
    if (!this.#wired) {
      this.#wired = true;

      realtime.on('InboxChanged', (status) => {
        this.unread = status.unread;
      });

      // The socket was deaf while it was down; the count may have moved in the meantime.
      realtime.onReconnected(() => void this.refresh());
    }

    await this.refresh();
  }

  async refresh(): Promise<void> {
    try {
      this.unread = (await notifications.status()).unread;
    } catch {
      /* The badge is a convenience. The next push or reconnect corrects it. */
    }
  }

  /**
   * Opening an issue is reading about it. Called by the issue page on load and whenever an entry for
   * it arrives while it is open, so the badge never counts what is already on screen.
   */
  async readIssue(issueId: Guid): Promise<void> {
    if (this.unread === 0) return;

    try {
      this.unread = (await notifications.readAll(issueId)).unread;
    } catch {
      /* Left unread, which is the safe way to be wrong. */
    }
  }

  reset(): void {
    this.unread = 0;
  }
}

export const inbox = new InboxBadge();
