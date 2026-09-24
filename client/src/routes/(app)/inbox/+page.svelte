<script lang="ts">
  import { ApiError, notifications as notificationsApi } from '$lib/api';
  import type { NotificationDto } from '$lib/api/types';
  import { prepareActivity } from '$lib/activity';
  import { chrome } from '$lib/chrome.svelte';
  import { byDay, plural } from '$lib/format';
  import { inbox } from '$lib/inbox.svelte';
  import { navigate } from '$lib/navigation.svelte';
  import { realtime } from '$lib/realtime/hub.svelte';
  import ActivityLine from '$components/activity/ActivityLine.svelte';
  import Icon from '$components/Icon.svelte';
  import { toasts } from '$components/toast.svelte';

  /**
   * What happened to the issues you follow, across every team.
   *
   * You follow an issue by filing it, being assigned it or commenting on it, or by pressing Follow on
   * it; someone else can add you too. Your own changes never land here.
   *
   * Opening an entry reads it — and everything else about the same issue, since the issue page shows
   * all of it at once. An entry can also be put back to unread, for "deal with this later".
   */
  const PAGE_SIZE = 50;

  let items = $state<NotificationDto[]>([]);
  let total = $state(0);
  let page = $state(1);
  let unreadOnly = $state(false);
  let loading = $state(true);
  let loadingMore = $state(false);
  let error = $state<string | null>(null);

  const days = $derived(byDay(items, (item) => item.createdAt));
  const hasMore = $derived(items.length < total);

  let generation = 0;

  async function load() {
    const mine = ++generation;
    loading = true;
    error = null;

    try {
      const result = await notificationsApi.list({ unread: unreadOnly || undefined, page: 1, pageSize: PAGE_SIZE });
      await prepareActivity(result.items.map((item) => item.event));
      if (mine !== generation) return;

      items = result.items;
      total = result.totalCount;
      page = 1;
    } catch (failure) {
      if (mine === generation) error = failure instanceof ApiError ? failure.message : 'Could not load your inbox.';
    } finally {
      if (mine === generation) loading = false;
    }
  }

  async function loadMore() {
    if (loadingMore || !hasMore) return;

    const mine = generation;
    loadingMore = true;

    try {
      const result = await notificationsApi.list({
        unread: unreadOnly || undefined,
        page: page + 1,
        pageSize: PAGE_SIZE
      });
      await prepareActivity(result.items.map((item) => item.event));
      if (mine !== generation) return;

      const seen = new Set(items.map((item) => item.id));
      items = [...items, ...result.items.filter((item) => !seen.has(item.id))];
      total = result.totalCount;
      page += 1;
    } catch (failure) {
      toasts.error(failure instanceof ApiError ? failure.message : 'Could not load older entries.');
    } finally {
      loadingMore = false;
    }
  }

  $effect(() => {
    void unreadOnly;
    void load();
  });

  $effect(() =>
    realtime.on('NotificationChanged', async (change) => {
      const entity = change.entity;
      if (change.kind !== 'Created' || !entity || items.some((item) => item.id === entity.id)) return;

      await prepareActivity([entity.event]);
      items = [entity, ...items];
      total += 1;
    })
  );

  // Everything read somewhere else — another tab's "mark all read", or the issue opened there. The
  // entries themselves are not pushed, so the list is brought back in line with the count.
  $effect(() =>
    realtime.on('InboxChanged', (status) => {
      const unreadHere = items.filter((item) => !item.readAt).length;
      if (status.unread === 0 && unreadHere > 0) {
        items = items.map((item) => (item.readAt ? item : { ...item, readAt: new Date().toISOString() }));
      } else if (status.unread < unreadHere) {
        void load();
      }
    })
  );

  $effect(() => realtime.onReconnected(() => void load()));

  $effect(() => {
    chrome.set({
      title: 'Inbox',
      subtitle: 'Issues you follow, across every team',
      status: loading ? 'Loading…' : inbox.unread > 0 ? `${inbox.unread} unread` : 'All caught up',
      actions: toolbar,
      commands: [
        {
          label: 'Mark all read',
          icon: 'check',
          keywords: ['clear', 'inbox', 'notifications'],
          disabled: inbox.unread === 0,
          run: () => void readAll()
        },
        {
          label: unreadOnly ? 'Show everything' : 'Show unread only',
          icon: 'filter',
          keywords: ['filter', 'unread'],
          run: () => (unreadOnly = !unreadOnly)
        }
      ]
    });
    chrome.refresh = load;
    chrome.busy = loading;

    return () => chrome.clear();
  });

  function markLocally(ids: Set<string>, read: boolean) {
    const at = read ? new Date().toISOString() : null;
    items = items.map((item) => (ids.has(item.id) ? { ...item, readAt: at } : item));
  }

  async function open(item: NotificationDto) {
    // The issue page reads everything about the issue as it opens; this only has to look right
    // until it does.
    markLocally(new Set(items.filter((other) => other.issue.id === item.issue.id).map((other) => other.id)), true);
    await navigate(`/issues/${item.issue.key}`);
  }

  async function toggle(item: NotificationDto) {
    const read = !item.readAt;
    markLocally(new Set([item.id]), read);

    try {
      const saved = await notificationsApi.setRead(item.id, read);
      items = items.map((candidate) => (candidate.id === saved.id ? saved : candidate));
    } catch (failure) {
      markLocally(new Set([item.id]), !read);
      toasts.error(failure instanceof ApiError ? failure.message : 'That could not be changed.');
    }
  }

  async function readAll() {
    try {
      inbox.unread = (await notificationsApi.readAll()).unread;
      markLocally(new Set(items.map((item) => item.id)), true);
      if (unreadOnly) void load();
    } catch (failure) {
      toasts.error(failure instanceof ApiError ? failure.message : 'Marking everything read failed.');
    }
  }
</script>

{#snippet toolbar()}
  <div class="segmented" role="group" aria-label="Show">
    <button type="button" class:on={!unreadOnly} aria-pressed={!unreadOnly} onclick={() => (unreadOnly = false)}>
      All
    </button>
    <button type="button" class:on={unreadOnly} aria-pressed={unreadOnly} onclick={() => (unreadOnly = true)}>
      Unread
    </button>
  </div>

  <button type="button" class="btn btn-sm" onclick={() => void readAll()} disabled={inbox.unread === 0}>
    <Icon name="check" size={13} />
    Mark all read
  </button>
{/snippet}

<div class="page">
  {#if error && items.length === 0}
    <div class="alert alert-error"><Icon name="circle-alert" size={15} /><span>{error}</span></div>
  {:else if loading && items.length === 0}
    <div class="empty"><Icon name="loader-circle" size={20} class="spin" /></div>
  {:else if items.length === 0}
    <div class="empty">
      <Icon name="inbox" size={28} />
      {#if unreadOnly}
        <p class="empty-title">All caught up.</p>
        <p>Nothing unread in the issues you follow.</p>
      {:else}
        <p class="empty-title">Your inbox is empty.</p>
        <p>
          Follow an issue — or file, take or comment on one — and whatever happens to it next shows up
          here.
        </p>
      {/if}
    </div>
  {:else}
    {#each days as day (day.key)}
      <section>
        <header><h2>{day.label}</h2></header>

        <ol>
          {#each day.items as item (item.id)}
            <li class="entry" class:unread={!item.readAt}>
              <button type="button" class="open" onclick={() => void open(item)}>
                <span class="marker" aria-label={item.readAt ? undefined : 'Unread'}></span>
                <ActivityLine event={item.event} />
              </button>

              <button
                type="button"
                class="toggle"
                title={item.readAt ? 'Mark unread' : 'Mark read'}
                aria-label={item.readAt ? 'Mark unread' : 'Mark read'}
                onclick={() => void toggle(item)}>
                <Icon name={item.readAt ? 'circle' : 'check'} size={13} />
              </button>
            </li>
          {/each}
        </ol>
      </section>
    {/each}

    {#if hasMore}
      <div class="more">
        <button type="button" class="btn btn-sm" onclick={() => void loadMore()} disabled={loadingMore}>
          {loadingMore ? 'Loading…' : `Show older (${plural(total - items.length, 'more entry', 'more entries')})`}
        </button>
      </div>
    {/if}
  {/if}
</div>

<style>
  .page {
    height: 100%;
    padding-bottom: var(--s-8);
    overflow-y: auto;
  }

  section {
    border-bottom: 1px solid var(--split);
    background: var(--bg-surface);
  }

  header {
    position: sticky;
    top: 0;
    z-index: 1;
    display: flex;
    align-items: center;
    height: var(--row-h);
    padding: 0 var(--s-5);
    border-bottom: 1px solid var(--split);
    background: var(--bg-app);
  }

  h2 {
    font-size: var(--text-sm);
    font-weight: 600;
  }

  ol {
    padding: var(--s-1) 0 var(--s-2);
  }

  .entry {
    display: flex;
    align-items: center;
    padding-right: var(--s-4);
  }

  .entry:hover {
    background: var(--bg-hover);
  }

  .open {
    display: flex;
    flex: 1;
    align-items: center;
    gap: var(--s-3);
    min-width: 0;
    padding: var(--s-3) var(--s-3) var(--s-3) var(--s-4);
    border: 0;
    background: none;
    color: var(--fg-tertiary);
    font: inherit;
    text-align: left;
    cursor: pointer;
  }

  /* Read entries recede; unread ones keep the full contrast of the line. */
  .entry:not(.unread) .open :global(.sentence) {
    color: var(--fg-tertiary);
  }

  .marker {
    flex: none;
    width: 7px;
    height: 7px;
    border-radius: 50%;
  }

  .unread .marker {
    background: var(--accent);
  }

  .toggle {
    display: grid;
    flex: none;
    place-items: center;
    width: 24px;
    height: 24px;
    border: 0;
    border-radius: var(--radius-xs);
    background: none;
    color: var(--fg-tertiary);
    cursor: pointer;
    opacity: 0;
  }

  .entry:hover .toggle,
  .toggle:focus-visible {
    opacity: 1;
  }

  .toggle:hover {
    background: var(--bg-active);
    color: var(--fg);
  }

  .segmented {
    display: inline-flex;
    padding: 2px;
    border: 1px solid var(--border);
    border-radius: var(--radius-sm);
    background: var(--bg-sunken);
  }

  .segmented button {
    height: 22px;
    padding: 0 var(--s-4);
    border: 0;
    border-radius: var(--radius-xs);
    background: none;
    color: var(--fg-secondary);
    font-size: var(--text-sm);
    cursor: pointer;
  }

  .segmented button.on {
    background: var(--bg-surface);
    color: var(--fg);
    font-weight: 500;
    box-shadow: var(--shadow-sm);
  }

  .more {
    display: flex;
    justify-content: center;
    padding: var(--s-5);
  }
</style>
