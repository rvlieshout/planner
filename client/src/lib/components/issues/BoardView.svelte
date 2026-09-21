<script lang="ts">
  import Icon from '$components/Icon.svelte';
  import BoardCard from './BoardCard.svelte';
  import { layOut } from '$lib/board';
  import { drag } from '$lib/dnd.svelte';
  import { issueEditor } from '$lib/issues/editor.svelte';
  import { moveIssue } from '$lib/issues/move';
  import { STATE_TYPE } from '$lib/meta';
  import { workspace } from '$lib/workspace.svelte';
  import type { Guid, IssueSummary, WorkflowStateDto } from '$lib/api/types';

  /**
   * The board itself: lanes of columns, each a drop target, with a rule showing where a card would
   * land and the lane it would land in tinted around it.
   *
   * What a drop writes is `moveIssue`, shared with the list view of the same board — the two show the
   * same issues grouped the same way, so a card dragged between columns and a row dragged between
   * groups mean the same thing.
   */
  interface Props {
    states: WorkflowStateDto[];
    issues: IssueSummary[];
    /** Pre-selected on the issue a card in this board creates — set on a project's board. */
    projectId?: Guid | null;
    /** Pre-selected likewise while the board is narrowed to one milestone. */
    milestoneId?: Guid | null;
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
    milestoneId = null,
    teamId,
    canMove = true,
    onopen,
    onoptimistic
  }: Props = $props();

  let selectedId = $state<string | null>(null);

  const lanes = $derived(layOut(states, issues));

  // A board of one project need not repeat its name on every issue; a team's board does, because
  // "which project is this?" is the first thing you ask of a card you did not put there yourself.
  const showProject = $derived(!projectId);

  function press(event: PointerEvent, issue: IssueSummary) {
    selectedId = issue.id;
    if (!canMove) return;

    drag.press(event, issue, {
      ondrop: (target) =>
        moveIssue({
          issue,
          stateId: target.key,
          index: target.index ?? 0,
          states,
          issues,
          apply: onoptimistic
        })
    });
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
              onclick={() => issueEditor.create({ teamId, projectId, milestoneId })}>
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
                project={showProject ? workspace.projectNow(issue.projectId) : null}
                selected={selectedId === issue.id}
                onselect={(chosen) => (selectedId = chosen.id)}
                {onopen}
                onpress={press} />
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
  /*
   * A grid, not a row of cards: lanes share the width between them and meet on a single rule, so
   * the board fills the screen however many states the team has, and scrolls only once the lanes
   * reach their minimum.
   */
  .board {
    display: flex;
    height: 100%;
    overflow-x: auto;
  }

  /*
   * Resting appearance in a style rule, not on the element: a local value outranks every style
   * setter, so a lane painted inline could never be lit up by the drop-target class below it.
   */
  .lane {
    display: flex;
    flex: 1 0 0;
    flex-direction: column;
    min-width: 260px;
    height: 100%;
    border-right: 1px solid var(--border);
    background: var(--bg-sunken);
    transition: background var(--duration) var(--ease);
  }

  .lane:last-child {
    border-right: 0;
  }

  .lane.drop-target {
    background: var(--accent-subtle);
  }

  /* Todo over Backlog: an even split, so both halves stay on screen and a drag always has somewhere
     to go, rather than one column growing until the other is off the bottom. */
  .lane.split .column {
    flex: 1 1 50%;
    min-height: 0;
  }

  .column + .column {
    border-top: 1px solid var(--border);
  }

  .column {
    display: flex;
    flex: 1;
    flex-direction: column;
    min-height: 0;
    transition: background var(--duration) var(--ease);
  }

  /* An inset ring rather than a border, so lighting a column up never shifts its neighbours. */
  .column.drop-target {
    background: var(--accent-subtle-hover);
    box-shadow: inset 0 0 0 1px var(--accent);
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
