<script lang="ts">
  import { activity as activityApi, ApiError } from '$lib/api';
  import type { ActivityEventDto, Guid } from '$lib/api/types';
  import { isPurged, prepareActivity } from '$lib/activity';
  import { chrome } from '$lib/chrome.svelte';
  import { byDay, plural } from '$lib/format';
  import { navigate } from '$lib/navigation.svelte';
  import { realtime } from '$lib/realtime/hub.svelte';
  import { workspace } from '$lib/workspace.svelte';
  import ActivityLine from '$components/activity/ActivityLine.svelte';
  import Icon from '$components/Icon.svelte';
  import Select from '$components/Select.svelte';
  import type { SelectOption } from '$components/select';

  /**
   * What has been happening in the current team, newest first, grouped by day.
   *
   * It pages backwards on request rather than loading everything: a feed is read from the top, and a
   * team's whole history is not something anyone scrolls to the bottom of. New events arrive over the
   * socket and are put on top, so an open feed never needs a refresh to stay current.
   */
  const PAGE_SIZE = 50;
  const ALL = '';

  let events = $state<ActivityEventDto[]>([]);
  let total = $state(0);
  let page = $state(1);
  let loading = $state(true);
  let loadingMore = $state(false);
  let error = $state<string | null>(null);
  let projectId = $state<Guid | ''>(ALL);

  const teamId = $derived(workspace.currentTeamId);
  const days = $derived(byDay(events, (event) => event.createdAt));
  const hasMore = $derived(events.length < total);

  /** Counts a request, so a slow answer for a team or project already left behind is thrown away. */
  let generation = 0;

  async function load() {
    const team = teamId;
    if (!team) return;

    const mine = ++generation;
    loading = true;
    error = null;

    try {
      const result = await activityApi.list({ teamId: team, projectId: projectId || undefined, page: 1, pageSize: PAGE_SIZE });
      await prepareActivity(result.items);
      if (mine !== generation) return;

      events = result.items;
      total = result.totalCount;
      page = 1;
    } catch (failure) {
      if (mine === generation) error = failure instanceof ApiError ? failure.message : 'Could not load the activity.';
    } finally {
      if (mine === generation) loading = false;
    }
  }

  async function loadMore() {
    const team = teamId;
    if (!team || loadingMore || !hasMore) return;

    const mine = generation;
    loadingMore = true;

    try {
      const result = await activityApi.list({
        teamId: team,
        projectId: projectId || undefined,
        page: page + 1,
        pageSize: PAGE_SIZE
      });
      await prepareActivity(result.items);
      if (mine !== generation) return;

      // Events that arrived live since the first page shift the pages along, so a row can come back
      // a second time. It is skipped rather than drawn twice.
      const seen = new Set(events.map((event) => event.id));
      events = [...events, ...result.items.filter((event) => !seen.has(event.id))];
      total = result.totalCount;
      page += 1;
    } catch (failure) {
      error = failure instanceof ApiError ? failure.message : 'Could not load older activity.';
    } finally {
      loadingMore = false;
    }
  }

  $effect(() => {
    void teamId;
    void projectId;
    void load();
  });

  // A project filter belongs to the team it was chosen in.
  $effect(() => {
    void teamId;
    projectId = ALL;
  });

  $effect(() =>
    realtime.on('ActivityRecorded', async (change) => {
      const event = change.entity;
      if (!event || event.teamId !== teamId) return;
      if (projectId && event.projectId !== projectId) return;
      if (events.some((existing) => existing.id === event.id)) return;

      await prepareActivity([event]);
      events = [event, ...events];
      total += 1;
    })
  );

  // An administrator cleared part of this team's history: the same rows leave this list.
  $effect(() =>
    realtime.on('ActivityPurged', (purge) => {
      if (purge.teamId !== teamId) return;

      const kept = events.filter((event) => !isPurged(purge, event));
      total = Math.max(0, total - (events.length - kept.length));
      events = kept;

      // Older pages may have lost rows this list never held, so the count is asked for again.
      if (hasMore) void load();
    })
  );

  $effect(() => realtime.onReconnected(() => void load()));

  $effect(() => {
    chrome.set({
      title: 'Activity',
      subtitle: workspace.currentTeam?.name,
      status: loading ? 'Loading…' : `${plural(total, 'event')}${hasMore ? `, ${events.length} shown` : ''}`,
      actions: toolbar
    });
    chrome.refresh = load;
    chrome.busy = loading;

    return () => chrome.clear();
  });

  const projectOptions = $derived<SelectOption<Guid | ''>[]>([
    { value: ALL, label: 'All projects', icon: 'folder', color: 'var(--fg-tertiary)' },
    ...workspace.projects
      .filter((project) => !project.archivedAt)
      .map((project) => ({ value: project.id, label: project.name, color: project.color }))
  ]);

  function open(event: ActivityEventDto) {
    if (event.issue) void navigate(`/issues/${event.issue.key}`);
    else if (event.entityType === 'project' && event.projectId) void navigate(`/projects/${event.projectId}`);
  }

  const opens = (event: ActivityEventDto) =>
    Boolean(event.issue) || (event.entityType === 'project' && Boolean(event.projectId));
</script>

{#snippet toolbar()}
  <Select options={projectOptions} value={projectId} onchange={(value) => (projectId = value)} label="Project" />
{/snippet}

<div class="page">
  {#if !teamId}
    <div class="empty">
      <Icon name="activity" size={28} />
      <p class="empty-title">You are not in a team yet.</p>
      <p>A team's activity shows up here once you are added to one.</p>
    </div>
  {:else if error && events.length === 0}
    <div class="alert alert-error"><Icon name="circle-alert" size={15} /><span>{error}</span></div>
  {:else if loading && events.length === 0}
    <div class="empty"><Icon name="loader-circle" size={20} class="spin" /></div>
  {:else if events.length === 0}
    <div class="empty">
      <Icon name="activity" size={28} />
      <p class="empty-title">Nothing has happened here yet.</p>
      <p>Issues filed, moved, assigned and discussed show up here as they happen.</p>
    </div>
  {:else}
    {#each days as day (day.key)}
      <section>
        <header><h2>{day.label}</h2></header>

        <ol>
          {#each day.items as event (event.id)}
            <li>
              {#if opens(event)}
                <button type="button" class="row" onclick={() => open(event)}>
                  <ActivityLine {event} />
                </button>
              {:else}
                <div class="row"><ActivityLine {event} /></div>
              {/if}
            </li>
          {/each}
        </ol>
      </section>
    {/each}

    {#if hasMore}
      <div class="more">
        <button type="button" class="btn btn-sm" onclick={() => void loadMore()} disabled={loadingMore}>
          {loadingMore ? 'Loading…' : 'Show older'}
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

  .row {
    display: block;
    width: 100%;
    padding: var(--s-3) var(--s-5);
    border: 0;
    background: none;
    color: inherit;
    font: inherit;
    text-align: left;
  }

  button.row {
    cursor: pointer;
  }

  button.row:hover {
    background: var(--bg-hover);
  }

  .more {
    display: flex;
    justify-content: center;
    padding: var(--s-5);
  }
</style>
