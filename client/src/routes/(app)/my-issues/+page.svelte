<script lang="ts">
  import { all, issues as issuesApi, ApiError } from '$lib/api';
  import type { IssueSummary, WorkflowStateType } from '$lib/api/types';
  import { session } from '$lib/auth/session.svelte';
  import { chrome } from '$lib/chrome.svelte';
  import { drag } from '$lib/dnd.svelte';
  import { applyChange, announce, onIssueChange } from '$lib/issues/changes';
  import { issueEditor } from '$lib/issues/editor.svelte';
  import { STATE_TYPE, STATE_TYPE_ORDER } from '$lib/meta';
  import { plural } from '$lib/format';
  import { navigate } from '$lib/navigation.svelte';
  import { realtime } from '$lib/realtime/hub.svelte';
  import { workspace } from '$lib/workspace.svelte';
  import { toasts } from '$components/toast.svelte';
  import Icon from '$components/Icon.svelte';
  import IssueRow from '$components/issues/IssueRow.svelte';

  /**
   * Everything assigned to you, across *every* team — not just the one on screen.
   *
   * It groups by workflow state **type** rather than by state, because these issues come from teams
   * whose columns do not line up. A drop therefore resolves to that issue's own team's first state of
   * the type: the same "Done" its own board would have moved it to.
   */
  let issues = $state<IssueSummary[]>([]);
  let loading = $state(true);
  let error = $state<string | null>(null);
  let selectedId = $state<string | null>(null);

  const groups = $derived(
    STATE_TYPE_ORDER.map((type) => ({
      type,
      meta: STATE_TYPE[type],
      issues: issues
        .filter((issue) => issue.stateType === type)
        .sort(
          (a, b) =>
            a.teamId.localeCompare(b.teamId) ||
            new Date(b.updatedAt).getTime() - new Date(a.updatedAt).getTime()
        )
    })).filter((group) => group.issues.length > 0 || group.type === 'Started' || group.type === 'Unstarted')
  );

  async function load() {
    const userId = session.user?.id;
    if (!userId) return;

    loading = true;
    error = null;

    try {
      issues = await all((page, pageSize) =>
        issuesApi.list({ assigneeId: [userId], sort: '-updatedAt', page, pageSize })
      );
    } catch (failure) {
      error = failure instanceof ApiError ? failure.message : 'Could not load your issues.';
    } finally {
      loading = false;
    }
  }

  $effect(() => {
    void load();
  });

  // Applied in place rather than by refetching, from both the socket and this client's own saves.
  $effect(() =>
    onIssueChange((change) => {
      issues = applyChange(issues, change, (issue) => issue.assignee?.id === session.user?.id);
    })
  );

  $effect(() => realtime.onReconnected(() => void load()));

  $effect(() => {
    chrome.set({
      title: 'My Issues',
      subtitle: 'Assigned to you across every team',
      status: loading ? 'Loading…' : plural(issues.length, 'issue'),
      actions: toolbar
    });
    chrome.refresh = load;
    chrome.busy = loading;

    return () => chrome.clear();
  });

  function open(issue: IssueSummary) {
    void navigate(`/issues/${issue.key}`);
  }

  /* ---------------------------------------------------------------- drag ---- */

  function press(event: PointerEvent, issue: IssueSummary) {
    selectedId = issue.id;

    drag.press(event, issue, {
      // A group with no order to land in: a drop sets the state and nothing else, so there is no gap
      // to point at and no rule to draw.
      ordered: false,
      accepts: (key) => key !== issue.stateType,
      ondrop: (target) => move(issue, target.key as WorkflowStateType)
    });
  }

  async function move(issue: IssueSummary, type: WorkflowStateType) {
    const state = await workspace.stateOfType(issue.teamId, type);

    if (!state) {
      toasts.error(`${issue.key}'s team has no ${STATE_TYPE[type].label.toLowerCase()} column.`);
      return;
    }

    // Moved first and told the server afterwards: a card that waits for a round trip feels broken,
    // and the echo of the move is the same idempotent upsert as any other change.
    const previous = issues;
    issues = issues.map((candidate) =>
      candidate.id === issue.id
        ? { ...candidate, stateId: state.id, stateName: state.name, stateType: state.type, stateColor: state.color }
        : candidate
    );

    try {
      announce('Updated', await issuesApi.move(issue.id, { stateId: state.id }));
    } catch (failure) {
      issues = previous;
      toasts.error(failure instanceof ApiError ? failure.message : 'That move was refused.');
    }
  }
</script>

{#snippet toolbar()}
  <button
    type="button"
    class="btn btn-sm"
    onclick={() => issueEditor.create({ teamId: workspace.currentTeamId ?? undefined })}
    disabled={!workspace.currentTeamId}>
    <Icon name="plus" size={13} />
    New issue
  </button>
{/snippet}

<div class="page">
  {#if error}
    <div class="alert alert-error"><Icon name="circle-alert" size={15} /><span>{error}</span></div>
  {:else if loading && issues.length === 0}
    <div class="empty"><Icon name="loader-circle" size={20} class="spin" /></div>
  {:else if issues.length === 0}
    <div class="empty">
      <Icon name="circle-check" size={28} />
      <p class="empty-title">Nothing is assigned to you.</p>
      <p>Issues assigned to you show up here, from every team you are in.</p>
    </div>
  {:else}
    {#each groups as group (group.type)}
      <section class="group" class:drop-target={drag.isTarget(group.type)} data-drop-key={group.type}>
        <header>
          <span style:color={group.meta.color}><Icon name={group.meta.icon} size={14} /></span>
          <h2>{group.meta.label}</h2>
          <span class="badge">{group.issues.length}</span>
        </header>

        <div class="rows">
          {#each group.issues as issue (issue.id)}
            <IssueRow
              {issue}
              draggable
              selected={selectedId === issue.id}
              onselect={(selected) => (selectedId = selected.id)}
              onopen={open}
              onpress={press} />
          {:else}
            <p class="none">Nothing here.</p>
          {/each}
        </div>
      </section>
    {/each}
  {/if}
</div>

<style>
  .page {
    height: 100%;
    padding-bottom: var(--s-8);
    overflow-y: auto;
  }

  /*
   * The resting appearance is set here rather than on the element, so the drop state can be a more
   * specific rule that actually wins. An inline background on the section could never be lit up by
   * adding a class to it.
   */
  .group {
    border-bottom: 1px solid var(--split);
    background: var(--bg-surface);
    transition: background var(--duration) var(--ease);
  }

  .group.drop-target {
    background: var(--accent-subtle);
    box-shadow: inset 2px 0 0 var(--accent);
  }

  header {
    position: sticky;
    top: 0;
    z-index: 1;
    display: flex;
    align-items: center;
    gap: var(--s-3);
    height: var(--row-h);
    padding: 0 var(--s-5);
    border-bottom: 1px solid var(--split);
    background: var(--bg-app);
  }

  h2 {
    font-size: var(--text-sm);
    font-weight: 600;
  }

  .rows {
    min-height: 34px;
    padding: var(--s-1) 0 var(--s-2);
  }

  .none {
    padding: var(--s-3) var(--s-5);
    color: var(--fg-tertiary);
    font-size: var(--text-sm);
  }
</style>
