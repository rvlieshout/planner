<script lang="ts">
  import Icon from '$components/Icon.svelte';
  import BoardCard from './BoardCard.svelte';
  import { layOut, type BoardColumn } from '$lib/board';
  import { drag, moveAnchors } from '$lib/dnd.svelte';
  import { announce } from '$lib/issues/changes';
  import { issueEditor } from '$lib/issues/editor.svelte';
  import { STATE_TYPE } from '$lib/meta';
  import { ApiError, issues as issuesApi } from '$lib/api';
  import type { Guid, IssueSummary, WorkflowStateDto } from '$lib/api/types';
  import { toasts } from '$components/toast.svelte';

  /**
   * The board itself: lanes of columns, each a drop target, with a rule showing where a card would
   * land and the lane it would land in tinted around it.
   *
   * The board moves the card first and tells the server afterwards. A drag that waits for a round trip
   * before the card lands feels broken, and the realtime echo of the move is the same idempotent
   * upsert as any other change, so it only confirms what is already on screen. A refusal puts the
   * board back the way the server sees it.
   */
  interface Props {
    states: WorkflowStateDto[];
    issues: IssueSummary[];
    /** Pre-selected on the issue a card in this board creates — set on a project's board. */
    projectId?: Guid | null;
    teamId: Guid;
    /** Off for a viewer or a guest, who may read a board but not rearrange it. */
    canMove?: boolean;
    onopen: (issue: IssueSummary) => void;
    /** Replaces the board's issues optimistically, and again if the server refuses. */
    onoptimistic: (issues: IssueSummary[]) => void;
  }

  let {
    states,
    issues,
    projectId = null,
    teamId,
    canMove = true,
    onopen,
    onoptimistic
  }: Props = $props();

  let selectedId = $state<string | null>(null);

  const lanes = $derived(layOut(states, issues));

  function press(event: PointerEvent, issue: IssueSummary, column: BoardColumn) {
    selectedId = issue.id;
    if (!canMove) return;

    drag.press(event, issue, {
      ondrop: (target) => move(issue, target.key, target.index ?? 0, column)
    });
  }

  async function move(issue: IssueSummary, stateId: string, index: number, from: BoardColumn) {
    const target = states.find((state) => state.id === stateId);
    if (!target) return;

    const column = lanes.flatMap((lane) => lane.columns).find((c) => c.state.id === stateId);
    const anchors = moveAnchors(column?.issues ?? [], index, issue.id);

    // Dropping a card back where it already was: same column, same two neighbours. Nothing to write.
    if (stateId === from.state.id) {
      const current = from.issues.findIndex((candidate) => candidate.id === issue.id);

      if (
        (from.issues[current - 1]?.id ?? null) === anchors.afterIssueId &&
        (from.issues[current + 1]?.id ?? null) === anchors.beforeIssueId
      ) {
        return;
      }
    }

    const previous = issues;

    // Put it where it was dropped straight away, with a rank between its new neighbours so the
    // optimistic order matches the one the server is about to write.
    const after = column?.issues.find((i) => i.id === anchors.afterIssueId);
    const before = column?.issues.find((i) => i.id === anchors.beforeIssueId);
    const sortOrder = midpoint(after?.sortOrder, before?.sortOrder, column?.issues ?? []);

    onoptimistic(
      issues.map((candidate) =>
        candidate.id === issue.id
          ? {
              ...candidate,
              stateId: target.id,
              stateName: target.name,
              stateType: target.type,
              stateColor: target.color,
              sortOrder
            }
          : candidate
      )
    );

    try {
      const saved = await issuesApi.move(issue.id, {
        stateId: target.id,
        afterIssueId: anchors.afterIssueId,
        beforeIssueId: anchors.beforeIssueId
      });

      announce('Updated', saved);
    } catch (failure) {
      onoptimistic(previous);
      toasts.error(failure instanceof ApiError ? failure.message : 'That move was refused.');
    }
  }

  /** The rank a card takes between two neighbours — the same midpoint the server computes. */
  function midpoint(after: number | undefined, before: number | undefined, column: IssueSummary[]): number {
    if (after !== undefined && before !== undefined) return (after + before) / 2;
    if (after !== undefined) return after + 1;
    if (before !== undefined) return before - 1;

    return column.length === 0 ? 0 : Math.min(...column.map((issue) => issue.sortOrder)) - 1;
  }
</script>

<div class="board">
  {#each lanes as lane (lane.key)}
    {@const lit = lane.columns.some((column) => drag.isTarget(column.state.id))}
    <section class="lane" class:drop-target={lit} class:split={lane.columns.length > 1}>
      {#each lane.columns as column (column.state.id)}
        <div class="column" class:drop-target={drag.isTarget(column.state.id)}>
          <header>
            <span style:color={column.state.color}>
              <Icon name={STATE_TYPE[column.state.type].icon} size={13} />
            </span>
            <h3 class="truncate">{column.state.name}</h3>
            <span class="badge">{column.issues.length}</span>
            <button
              type="button"
              class="add"
              title="New issue in {column.state.name}"
              aria-label="New issue in {column.state.name}"
              onclick={() => issueEditor.create({ teamId, projectId })}>
              <Icon name="plus" size={13} />
            </button>
          </header>

          <div class="cards" data-drop-key={column.state.id}>
            {#if drag.isTarget(column.state.id) && drag.target?.offset !== null && drag.target?.offset !== undefined}
              <span class="indicator" style:top="{drag.target.offset}px"></span>
            {/if}

            {#each column.issues as issue (issue.id)}
              <BoardCard
                {issue}
                selected={selectedId === issue.id}
                onselect={(chosen) => (selectedId = chosen.id)}
                {onopen}
                onpress={(event, dragged) => press(event, dragged, column)} />
            {/each}
          </div>
        </div>
      {/each}
    </section>
  {:else}
    <div class="empty">
      <Icon name="layout-grid" size={28} />
      <p class="empty-title">This team has no workflow states.</p>
      <p>A team normally starts with six. Add them from the Teams page.</p>
    </div>
  {/each}
</div>

<style>
  .board {
    display: flex;
    gap: var(--s-4);
    height: 100%;
    padding: var(--s-4) var(--s-5);
    overflow-x: auto;
  }

  /*
   * Resting appearance in a style rule, not on the element: a local value outranks every style
   * setter, so a lane painted inline could never be lit up by the drop-target class below it.
   */
  .lane {
    display: flex;
    flex: none;
    flex-direction: column;
    gap: var(--s-3);
    width: 292px;
    height: 100%;
    padding: var(--s-2);
    border: 1px solid transparent;
    border-radius: var(--radius-md);
    transition:
      background var(--duration) var(--ease),
      border-color var(--duration) var(--ease);
  }

  .lane.drop-target {
    border-color: var(--accent-border);
    background: var(--accent-subtle);
  }

  /* Todo over Backlog: an even split, so both halves stay on screen and a drag always has somewhere
     to go, rather than one column growing until the other is off the bottom. */
  .lane.split .column {
    flex: 1 1 50%;
    min-height: 0;
  }

  .column {
    display: flex;
    flex: 1;
    flex-direction: column;
    min-height: 0;
    border: 1px solid var(--border);
    border-radius: var(--radius-sm);
    background: var(--bg-sunken);
  }

  .column.drop-target {
    border-color: var(--accent);
    background: var(--accent-subtle-hover);
  }

  header {
    display: flex;
    flex: none;
    align-items: center;
    gap: var(--s-2);
    height: var(--row-h);
    padding: 0 var(--s-2) 0 var(--s-3);
    border-bottom: 1px solid var(--border);
  }

  h3 {
    flex: 1;
    font-size: var(--text-sm);
    font-weight: 600;
  }

  .add {
    display: grid;
    flex: none;
    place-items: center;
    width: 20px;
    height: 20px;
    border: 0;
    border-radius: var(--radius-xs);
    background: none;
    color: var(--fg-tertiary);
    cursor: pointer;
  }

  .add:hover {
    background: var(--bg-active);
    color: var(--fg);
  }

  .cards {
    position: relative;
    display: flex;
    flex: 1;
    flex-direction: column;
    gap: var(--s-2);
    min-height: 60px;
    padding: var(--s-2);
    overflow-y: auto;
  }

  /* The rule and the move come from one walk of the rows, so what is shown can never point at a
     different gap from the one that is used. */
  .indicator {
    position: absolute;
    right: var(--s-2);
    left: var(--s-2);
    height: 2px;
    margin-top: -1px;
    border-radius: 1px;
    background: var(--accent);
  }

  .empty {
    width: 100%;
  }
</style>
