<script lang="ts">
  import { page } from '$app/state';
  import { all, ApiError, issues as issuesApi, projects as projectsApi } from '$lib/api';
  import type { Guid, IssueSummary, MilestoneDto, ProjectDto, WorkflowStateDto } from '$lib/api/types';
  import { boardSummary, layOut } from '$lib/board';
  import { chrome } from '$lib/chrome.svelte';
  import { Permission, session } from '$lib/auth/session.svelte';
  import { applyChange, onIssueChange } from '$lib/issues/changes';
  import { issueEditor } from '$lib/issues/editor.svelte';
  import { navigate } from '$lib/navigation.svelte';
  import { EMPTY_PROGRESS, rollUp, rollUpByMilestone } from '$lib/progress';
  import { realtime } from '$lib/realtime/hub.svelte';
  import { settings } from '$lib/settings.svelte';
  import { workspace } from '$lib/workspace.svelte';
  import { PROJECT_HEALTH, PROJECT_STATUS } from '$lib/meta';
  import { formatDate } from '$lib/format';
  import BoardView from '$components/issues/BoardView.svelte';
  import ListView from '$components/issues/ListView.svelte';
  import Icon from '$components/Icon.svelte';
  import Progress from '$components/Progress.svelte';

  /**
   * A project's board, with the project's own rollup and milestones above it. Board or list is the
   * one choice `settings.boardView` remembers, shared with the team's board.
   */
  const projectId = $derived(page.params.id!);

  let project = $state<ProjectDto | null>(null);
  let milestones = $state<MilestoneDto[]>([]);
  let issues = $state<IssueSummary[]>([]);
  let states = $state<WorkflowStateDto[]>([]);
  let loading = $state(true);
  let error = $state<string | null>(null);
  let showDetail = $state(true);
  /** The milestone the board is narrowed to, picked from the overview; null shows every issue. */
  let milestoneId = $state<Guid | null>(null);

  const canWrite = $derived(session.can(project?.teamId, Permission.Write));
  const asList = $derived(settings.boardView === 'list');

  /*
   * Counted from `issues` rather than read from `project.progress` and `milestone.progress`.
   *
   * Those two are snapshots from the moment the project and its milestones were fetched, so creating
   * an issue used to move the board and the status bar while leaving the rollup above them showing
   * the old numbers. Counting the list this page already holds keeps all three in step without
   * waiting for the socket — `issues` is the project's complete unfiltered set, the same scope the
   * server counts.
   */
  const milestoneProgress = $derived(rollUpByMilestone(issues));

  const picked = $derived(milestones.find((candidate) => candidate.id === milestoneId) ?? null);

  // Picking a milestone narrows the board, the status bar and the progress bar alike; the chips
  // keep their own counts, so the other milestones can still be compared at a glance.
  const visible = $derived(
    picked ? issues.filter((issue) => issue.milestoneId === picked.id) : issues
  );

  const progress = $derived(rollUp(visible));

  function toggleMilestone(id: Guid) {
    milestoneId = milestoneId === id ? null : id;
  }

  /*
   * The views compute their optimistic update from what they were handed, which is the narrowed
   * set while a milestone is picked. Folding it back by id keeps the issues it leaves out, so a
   * drag on a narrowed board cannot drop the rest of the project from the page.
   */
  function applyVisible(next: IssueSummary[]) {
    if (!picked) {
      issues = next;
      return;
    }

    const byId = new Map(next.map((issue) => [issue.id, issue]));
    issues = issues.map((issue) => byId.get(issue.id) ?? issue);
  }

  async function load() {
    loading = true;
    error = null;

    const requested = projectId;

    try {
      const loaded = await projectsApi.get(requested);
      if (projectId !== requested) return;

      project = loaded;

      const [loadedStates, loadedMilestones, loadedIssues] = await Promise.all([
        workspace.statesFor(loaded.teamId),
        projectsApi.milestones(loaded.id),
        all((p, pageSize) =>
          issuesApi.list({ projectId: loaded.id, sort: 'board', page: p, pageSize })
        )
      ]);

      if (projectId !== requested) return;

      states = loadedStates;
      milestones = loadedMilestones;
      issues = loadedIssues;
    } catch (failure) {
      error = failure instanceof ApiError ? failure.message : 'Could not load that project.';
    } finally {
      loading = false;
    }
  }

  $effect(() => {
    void projectId;
    milestoneId = null;
    void load();
  });

  $effect(() =>
    onIssueChange((change) => {
      issues = applyChange(issues, change, (issue) => issue.projectId === projectId);
    })
  );

  $effect(() => realtime.onReconnected(() => void load()));

  $effect(() => {
    chrome.set({
      title: project?.name ?? 'Project',
      subtitle: project ? PROJECT_STATUS[project.status].label : undefined,
      status: loading ? 'Loading…' : boardSummary(layOut(states, visible), asList ? 'group' : 'column'),
      actions: toolbar,
      commands: [
        {
          label: asList ? 'Board view' : 'List view',
          icon: asList ? 'layout-grid' : 'list',
          shortcut: 'v',
          keywords: ['switch', 'kanban', 'columns', 'rows', 'toggle'],
          run: () => settings.toggleBoardView()
        },
        {
          label: showDetail ? 'Hide overview' : 'Show overview',
          icon: showDetail ? 'chevron-down' : 'chevron-right',
          shortcut: 'o',
          keywords: ['details', 'milestones', 'toggle'],
          run: () => (showDetail = !showDetail)
        },
        {
          label: 'Project settings',
          icon: 'settings',
          keywords: ['edit', 'rename', 'milestones'],
          disabled: !canWrite,
          run: () => void navigate(`/projects/${projectId}/settings`)
        }
      ]
    });
    chrome.refresh = load;
    chrome.busy = loading;

    return () => chrome.clear();
  });
</script>

{#snippet toolbar()}
  {#if picked}
    <button
      type="button"
      class="btn btn-sm btn-quiet"
      onclick={() => (milestoneId = null)}
      title="Show every issue in the project">
      <Icon name="milestone" size={13} />
      {picked.name}
      <Icon name="x" size={13} />
    </button>
  {/if}

  <button
    type="button"
    class="btn btn-sm btn-quiet"
    onclick={() => settings.toggleBoardView()}
    title={asList ? 'Show as a board (V)' : 'Show as a list (V)'}>
    <Icon name={asList ? 'layout-grid' : 'list'} size={13} />
    {asList ? 'Board' : 'List'}
  </button>

  <button type="button" class="btn btn-sm btn-quiet" onclick={() => (showDetail = !showDetail)}>
    <Icon name={showDetail ? 'chevron-down' : 'chevron-right'} size={13} />
    Overview
  </button>

  <button
    type="button"
    class="btn btn-sm"
    onclick={() => void navigate(`/projects/${projectId}/settings`)}
    disabled={!canWrite}>
    <Icon name="settings" size={13} />
    Project
  </button>

  <button
    type="button"
    class="btn btn-sm"
    onclick={() => project && issueEditor.create({ teamId: project.teamId, projectId, milestoneId })}
    disabled={!canWrite}>
    <Icon name="plus" size={13} />
    New issue
  </button>
{/snippet}

{#if error}
  <div class="alert alert-error"><Icon name="circle-alert" size={15} /><span>{error}</span></div>
{:else if loading && !project}
  <div class="empty"><Icon name="loader-circle" size={20} class="spin" /></div>
{:else if project}
  <div class="layout">
    {#if showDetail}
      <section class="overview">
        <div class="headline">
          <span class="swatch" style:background={project.color}></span>
          <h2>{project.name}</h2>

          <span class="chip" style:color={PROJECT_STATUS[project.status].color}>
            <Icon name={PROJECT_STATUS[project.status].icon} size={12} />
            {PROJECT_STATUS[project.status].label}
          </span>

          <span class="chip" style:color={PROJECT_HEALTH[project.health].color}>
            <Icon name={PROJECT_HEALTH[project.health].icon} size={12} />
            {PROJECT_HEALTH[project.health].label}
          </span>

          {#if project.targetDate}
            <span class="chip">
              <Icon name="calendar" size={12} />
              {formatDate(project.targetDate)}
            </span>
          {/if}

          {#if project.lead}
            <span class="chip"><Icon name="user" size={12} />{project.lead.displayName}</span>
          {/if}
        </div>

        {#if project.summary}<p class="summary">{project.summary}</p>{/if}

        <Progress {progress} />

        {#if milestones.length > 0}
          <div class="milestones">
            {#each milestones as milestone (milestone.id)}
              {@const rollup = milestoneProgress.get(milestone.id) ?? EMPTY_PROGRESS}
              <button
                type="button"
                class="milestone"
                class:active={milestoneId === milestone.id}
                aria-pressed={milestoneId === milestone.id}
                title={milestoneId === milestone.id
                  ? 'Show every issue in the project'
                  : `Show only the issues in ${milestone.name}`}
                onclick={() => toggleMilestone(milestone.id)}>
                <Icon name="milestone" size={12} />
                <span class="truncate">{milestone.name}</span>
                {#if milestone.targetDate}
                  <span class="muted">{formatDate(milestone.targetDate)}</span>
                {/if}
                <span class="badge">{rollup.completed}/{rollup.total}</span>
              </button>
            {/each}
          </div>
        {/if}
      </section>
    {/if}

    <div class="board-area">
      {#if asList}
        <ListView
          {states}
          issues={visible}
          teamId={project.teamId}
          {projectId}
          {milestoneId}
          canMove={canWrite}
          onopen={(issue) => void navigate(`/issues/${issue.key}`)}
          onoptimistic={applyVisible} />
      {:else}
        <BoardView
          {states}
          issues={visible}
          teamId={project.teamId}
          {projectId}
          {milestoneId}
          canMove={canWrite}
          onopen={(issue) => void navigate(`/issues/${issue.key}`)}
          onoptimistic={applyVisible} />
      {/if}
    </div>
  </div>
{/if}

<style>
  .layout {
    display: flex;
    flex-direction: column;
    height: 100%;
  }

  .overview {
    display: flex;
    flex: none;
    flex-direction: column;
    gap: var(--s-4);
    padding: var(--s-5);
    border-bottom: 1px solid var(--border);
    background: var(--bg-app);
  }

  .headline {
    display: flex;
    flex-wrap: wrap;
    align-items: center;
    gap: var(--s-3);
  }

  .swatch {
    width: 12px;
    height: 12px;
    border-radius: 3px;
  }

  h2 {
    margin-right: var(--s-2);
    font-size: var(--text-lg);
    letter-spacing: -0.01em;
  }

  .summary {
    max-width: 78ch;
    color: var(--fg-secondary);
    line-height: var(--leading-relaxed);
  }

  .milestones {
    display: flex;
    flex-wrap: wrap;
    gap: var(--s-3);
  }

  .milestone {
    display: flex;
    align-items: center;
    gap: var(--s-2);
    max-width: 280px;
    height: var(--control-h-sm);
    padding: 0 var(--s-3);
    border: 1px solid var(--border);
    border-radius: var(--radius-sm);
    background: none;
    color: var(--fg-secondary);
    font: inherit;
    font-size: var(--text-sm);
    cursor: pointer;
    transition:
      background var(--duration) var(--ease),
      border-color var(--duration) var(--ease);
  }

  .milestone:hover {
    background: var(--bg-hover);
    color: var(--fg);
  }

  .milestone.active {
    border-color: var(--accent);
    background: var(--accent-subtle);
    color: var(--fg);
  }

  .board-area {
    flex: 1;
    min-height: 0;
  }
</style>
