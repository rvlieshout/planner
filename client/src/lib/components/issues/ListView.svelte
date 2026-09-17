<script lang="ts">
  import Icon from '$components/Icon.svelte';
  import IssueRow from './IssueRow.svelte';
  import { layOut } from '$lib/board';
  import { drag } from '$lib/dnd.svelte';
  import { issueEditor } from '$lib/issues/editor.svelte';
  import { moveIssue } from '$lib/issues/move';
  import { STATE_TYPE } from '$lib/meta';
  import type { Guid, IssueSummary, WorkflowStateDto } from '$lib/api/types';

  /**
   * The same board as rows rather than cards: one group per workflow state, in the order the columns
   * stand in, each row the dense line My Issues uses.
   *
   * It is the view for reading a board rather than pushing it along — every title in full, scanned
   * top to bottom instead of across — so it is the same issues, the same groups and the same drops,
   * drawn differently. Dragging a row between groups moves the issue exactly as dragging its card
   * between columns does, which is why both views hand the drop to the same place.
   */
  interface Props {
    states: WorkflowStateDto[];
    issues: IssueSummary[];
    /** Pre-selected on the issue a group in this list creates — set on a project's board. */
    projectId?: Guid | null;
    teamId: Guid;
    /** Off for a viewer or a guest, who may read a board but not rearrange it. */
    canMove?: boolean;
    onopen: (issue: IssueSummary) => void;
    /** Replaces the view's issues optimistically, and again if the server refuses. */
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

  // Laid out by the board's own rule and then flattened, so Todo still comes before Backlog and a
  // team's own column order is the order these groups are read in.
  const groups = $derived(layOut(states, issues).flatMap((lane) => lane.columns));

  function press(event: PointerEvent, issue: IssueSummary) {
    selectedId = issue.id;
    if (!canMove) return;

    drag.press(event, issue, {
      // Every group takes every issue, its own included: a row dragged within its group is a reorder.
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

<div class="list">
  {#each groups as group (group.state.id)}
    <section class="group" class:drop-target={drag.isTarget(group.state.id)}>
      <header>
        <span style:color={group.state.color}>
          <Icon name={STATE_TYPE[group.state.type].icon} size={14} />
        </span>
        <h2 class="truncate">{group.state.name}</h2>
        <span class="badge">{group.issues.length}</span>

        <button
          type="button"
          class="add"
          title="New issue in {group.state.name}"
          aria-label="New issue in {group.state.name}"
          onclick={() => issueEditor.create({ teamId, projectId })}>
          <Icon name="plus" size={13} />
        </button>
      </header>

      <div class="rows" data-drop-key={group.state.id}>
        {#if drag.isTarget(group.state.id) && drag.target?.offset !== null && drag.target?.offset !== undefined}
          <span class="indicator" style:top="{drag.target.offset}px"></span>
        {/if}

        {#each group.issues as issue (issue.id)}
          <IssueRow
            {issue}
            draggable={canMove}
            selected={selectedId === issue.id}
            onselect={(chosen) => (selectedId = chosen.id)}
            {onopen}
            onpress={press} />
        {:else}
          <p class="none">Nothing here.</p>
        {/each}
      </div>
    </section>
  {:else}
    <div class="empty">
      <Icon name="list" size={28} />
      <p class="empty-title">This team has no workflow states.</p>
      <p>A team normally starts with six. Add them from the Teams page.</p>
    </div>
  {/each}
</div>

<style>
  .list {
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
    /* Shrinkable, so a long state name truncates rather than pushing the count off the row. */
    min-width: 0;
    font-size: var(--text-sm);
    font-weight: 600;
  }

  .add {
    display: grid;
    flex: none;
    place-items: center;
    width: 20px;
    height: 20px;
    margin-left: auto;
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

  .rows {
    position: relative;
    min-height: 34px;
    padding: var(--s-1) 0 var(--s-2);
  }

  /* The rule and the move come from one walk of the rows, so what is shown can never point at a
     different gap from the one that is used. */
  .indicator {
    position: absolute;
    right: var(--s-5);
    left: var(--s-5);
    height: 2px;
    margin-top: -1px;
    border-radius: 1px;
    background: var(--accent);
  }

  .none {
    padding: var(--s-3) var(--s-5);
    color: var(--fg-tertiary);
    font-size: var(--text-sm);
  }
</style>
