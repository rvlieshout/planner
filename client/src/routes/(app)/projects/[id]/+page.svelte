<script lang="ts">
  import { page } from '$app/state';
  import { all, ApiError, issues as issuesApi, projects as projectsApi } from '$lib/api';
  import type { IssueSummary, MilestoneDto, ProjectDto, WorkflowStateDto } from '$lib/api/types';
  import { boardSummary, layOut } from '$lib/board';
  import { chrome } from '$lib/chrome.svelte';
  import { Permission, session } from '$lib/auth/session.svelte';
  import { applyChange, onIssueChange } from '$lib/issues/changes';
  import { issueEditor } from '$lib/issues/editor.svelte';
  import { navigate } from '$lib/navigation.svelte';
  import { realtime } from '$lib/realtime/hub.svelte';
  import { workspace } from '$lib/workspace.svelte';
  import { PROJECT_HEALTH, PROJECT_STATUS } from '$lib/meta';
  import { formatDate } from '$lib/format';
  import BoardView from '$components/issues/BoardView.svelte';
  import Icon from '$components/Icon.svelte';
  import Progress from '$components/Progress.svelte';

  /** A project's board, with the project's own rollup and milestones above it. */
  const projectId = $derived(page.params.id!);

  let project = $state<ProjectDto | null>(null);
  let milestones = $state<MilestoneDto[]>([]);
  let issues = $state<IssueSummary[]>([]);
  let states = $state<WorkflowStateDto[]>([]);
  let loading = $state(true);
  let error = $state<string | null>(null);
  let showDetail = $state(true);

  const canWrite = $derived(session.can(project?.teamId, Permission.Write));

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
      status: loading ? 'Loading…' : boardSummary(layOut(states, issues)),
      actions: toolbar
    });
    chrome.refresh = load;
    chrome.busy = loading;

    return () => chrome.clear();
  });
</script>

{#snippet toolbar()}
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
    onclick={() => project && issueEditor.create({ teamId: project.teamId, projectId })}
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

        <Progress progress={project.progress} />

        {#if milestones.length > 0}
          <div class="milestones">
            {#each milestones as milestone (milestone.id)}
              <div class="milestone">
                <Icon name="milestone" size={12} />
                <span class="truncate">{milestone.name}</span>
                {#if milestone.targetDate}
                  <span class="muted">{formatDate(milestone.targetDate)}</span>
                {/if}
                <span class="badge">{milestone.progress.completed}/{milestone.progress.total}</span>
              </div>
            {/each}
          </div>
        {/if}
      </section>
    {/if}

    <div class="board-area">
      <BoardView
        {states}
        {issues}
        teamId={project.teamId}
        {projectId}
        canMove={canWrite}
        onopen={(issue) => void navigate(`/issues/${issue.key}`)}
        onoptimistic={(next) => (issues = next)} />
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
    color: var(--fg-secondary);
    font-size: var(--text-sm);
  }

  .board-area {
    flex: 1;
    min-height: 0;
  }
</style>
