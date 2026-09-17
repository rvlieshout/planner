<script lang="ts">
  import { ApiError } from '$lib/api';
  import type { IssueSummary, WorkflowStateDto } from '$lib/api/types';
  import { boardSummary, layOut } from '$lib/board';
  import { chrome } from '$lib/chrome.svelte';
  import { session, Permission } from '$lib/auth/session.svelte';
  import { applyChange, onIssueChange } from '$lib/issues/changes';
  import { issueEditor } from '$lib/issues/editor.svelte';
  import { navigate } from '$lib/navigation.svelte';
  import { realtime } from '$lib/realtime/hub.svelte';
  import { settings } from '$lib/settings.svelte';
  import { loadBoardIssues, workspace } from '$lib/workspace.svelte';
  import BoardView from '$components/issues/BoardView.svelte';
  import ListView from '$components/issues/ListView.svelte';
  import Icon from '$components/Icon.svelte';

  /**
   * The current team's board: one column per workflow state, live over the socket — or the same
   * states as groups of rows, which is the choice `settings.boardView` remembers for every board.
   */
  let issues = $state<IssueSummary[]>([]);
  let states = $state<WorkflowStateDto[]>([]);
  let loading = $state(true);
  let error = $state<string | null>(null);

  const teamId = $derived(workspace.currentTeamId);
  const team = $derived(workspace.currentTeam);
  const canMove = $derived(session.can(teamId, Permission.Write));
  const asList = $derived(settings.boardView === 'list');

  async function load() {
    if (!teamId) {
      issues = [];
      states = [];
      loading = false;
      return;
    }

    loading = true;
    error = null;

    const requested = teamId;

    try {
      const [loadedStates, loadedIssues] = await Promise.all([
        workspace.statesFor(requested),
        loadBoardIssues(requested)
      ]);

      // A slower answer for a team the user has already switched away from must not land here.
      if (workspace.currentTeamId !== requested) return;

      states = loadedStates;
      issues = loadedIssues;
    } catch (failure) {
      error = failure instanceof ApiError ? failure.message : 'Could not load the board.';
    } finally {
      loading = false;
    }
  }

  $effect(() => {
    void teamId;
    void load();
  });

  $effect(() =>
    onIssueChange((change) => {
      if (change.teamId !== teamId) return;
      issues = applyChange(issues, change, (issue) => issue.teamId === teamId);
    })
  );

  $effect(() => realtime.onReconnected(() => void load()));

  $effect(() => {
    chrome.set({
      title: team?.name ?? 'Board',
      subtitle: team?.key,
      status: loading ? 'Loading…' : boardSummary(layOut(states, issues), asList ? 'group' : 'column'),
      actions: toolbar,
      commands: [
        {
          label: asList ? 'Board view' : 'List view',
          icon: asList ? 'layout-grid' : 'list',
          shortcut: 'v',
          keywords: ['switch', 'kanban', 'columns', 'rows', 'toggle'],
          run: () => settings.toggleBoardView()
        }
      ]
    });
    chrome.refresh = load;
    chrome.busy = loading;

    return () => chrome.clear();
  });
</script>

{#snippet toolbar()}
  <button
    type="button"
    class="btn btn-sm btn-quiet"
    onclick={() => settings.toggleBoardView()}
    title={asList ? 'Show as a board (V)' : 'Show as a list (V)'}>
    <Icon name={asList ? 'layout-grid' : 'list'} size={13} />
    {asList ? 'Board' : 'List'}
  </button>

  <button
    type="button"
    class="btn btn-sm"
    onclick={() => teamId && issueEditor.create({ teamId })}
    disabled={!teamId || !canMove}>
    <Icon name="plus" size={13} />
    New issue
  </button>
{/snippet}

{#if !teamId}
  <div class="empty">
    <Icon name="users" size={28} />
    <p class="empty-title">You are not in any team yet.</p>
    <p>A team is where boards, projects and issues live. An administrator creates the first one.</p>
  </div>
{:else if error}
  <div class="alert alert-error"><Icon name="circle-alert" size={15} /><span>{error}</span></div>
{:else if loading && issues.length === 0}
  <div class="empty"><Icon name="loader-circle" size={20} class="spin" /></div>
{:else if asList}
  <ListView
    {states}
    {issues}
    teamId={teamId}
    {canMove}
    onopen={(issue) => void navigate(`/issues/${issue.key}`)}
    onoptimistic={(next) => (issues = next)} />
{:else}
  <BoardView
    {states}
    {issues}
    teamId={teamId}
    {canMove}
    onopen={(issue) => void navigate(`/issues/${issue.key}`)}
    onoptimistic={(next) => (issues = next)} />
{/if}
