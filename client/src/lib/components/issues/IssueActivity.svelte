<script lang="ts">
  import { all, issues as issuesApi } from '$lib/api';
  import type { ActivityEventDto, Guid } from '$lib/api/types';
  import { isPurged, prepareActivity } from '$lib/activity';
  import { realtime } from '$lib/realtime/hub.svelte';
  import ActivityLine from '$components/activity/ActivityLine.svelte';

  /**
   * How this issue got to where it is: filed, moved, assigned, relabelled, re-planned.
   *
   * Oldest first, reading down into the conversation below it. Comments are left out — they are right
   * there in full — and a long history shows its most recent entries with the rest a click away, since
   * the question is nearly always "what happened lately".
   */
  interface Props {
    issueId: Guid;
  }

  let { issueId }: Props = $props();

  const RECENT = 6;

  let events = $state<ActivityEventDto[]>([]);
  let expanded = $state(false);

  const history = $derived(
    events.filter((event) => event.action !== 'commented').sort((a, b) => a.createdAt.localeCompare(b.createdAt))
  );
  const hidden = $derived(expanded ? 0 : Math.max(0, history.length - RECENT));
  const shown = $derived(history.slice(hidden));

  let generation = 0;

  async function load() {
    const id = issueId;
    const mine = ++generation;

    try {
      const loaded = await all((page, pageSize) => issuesApi.activity(id, { page, pageSize }));
      await prepareActivity(loaded);
      if (mine === generation) events = loaded;
    } catch {
      /* History is context. An issue page that cannot show it is still an issue page. */
    }
  }

  $effect(() => {
    void issueId;
    expanded = false;
    void load();
  });

  $effect(() =>
    realtime.on('ActivityRecorded', async (change) => {
      const event = change.entity;
      if (!event || event.issueId !== issueId || events.some((existing) => existing.id === event.id)) return;

      await prepareActivity([event]);
      events = [...events, event];
    })
  );

  $effect(() =>
    realtime.on('ActivityPurged', (purge) => {
      events = events.filter((event) => !isPurged(purge, event));
    })
  );

  $effect(() => realtime.onReconnected(() => void load()));
</script>

{#if history.length > 0}
  <section class="activity">
    <header class="section-header">
      <h2>Activity</h2>
    </header>

    <ol>
      {#if hidden > 0}
        <li>
          <button type="button" class="btn-link more" onclick={() => (expanded = true)}>
            Show {hidden} earlier {hidden === 1 ? 'change' : 'changes'}
          </button>
        </li>
      {/if}

      {#each shown as event (event.id)}
        <li><ActivityLine {event} showIssue={false} compact /></li>
      {/each}
    </ol>
  </section>
{/if}

<style>
  .activity {
    display: flex;
    flex-direction: column;
    gap: var(--s-3);
  }

  h2 {
    font-size: var(--text-md);
  }

  /* A thread through the glyphs, so the entries read as one sequence rather than a list of facts. */
  ol {
    position: relative;
    display: flex;
    flex-direction: column;
    gap: var(--s-3);
  }

  ol::before {
    position: absolute;
    top: 11px;
    bottom: 11px;
    left: 10.5px;
    width: 1px;
    background: var(--split);
    content: '';
  }

  li {
    position: relative;
  }

  .more {
    margin-left: calc(22px + var(--s-3));
    font-size: var(--text-sm);
  }
</style>
