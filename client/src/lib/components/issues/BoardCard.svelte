<script lang="ts">
  import AssigneePicker from './AssigneePicker.svelte';
  import Icon from '$components/Icon.svelte';
  import LabelChip from '$components/LabelChip.svelte';
  import PriorityIcon from '$components/PriorityIcon.svelte';
  import ProjectChip from '$components/ProjectChip.svelte';
  import { drag } from '$lib/dnd.svelte';
  import { formatDate, isOverdue } from '$lib/format';
  import type { IssueSummary, ProjectDto } from '$lib/api/types';

  /**
   * One issue on a board.
   *
   * Three rows at most: the key and its priority, the title, and whatever else is actually set. A
   * card with placeholders for everything an issue *could* have is a card you have to read rather
   * than scan.
   */
  interface Props {
    issue: IssueSummary;
    /** The issue's project, named beside its key — left out on a board that is already one project. */
    project?: ProjectDto | null;
    selected?: boolean;
    onselect?: (issue: IssueSummary) => void;
    onopen?: (issue: IssueSummary) => void;
    onpress?: (event: PointerEvent, issue: IssueSummary) => void;
  }

  let { issue, project = null, selected = false, onselect, onopen, onpress }: Props = $props();

  const overdue = $derived(isOverdue(issue.dueDate));
  const hasFooter = $derived(
    issue.labels.length > 0 ||
      Boolean(issue.dueDate) ||
      Boolean(issue.estimate) ||
      issue.subIssueCount > 0 ||
      issue.commentCount > 0
  );
</script>

<!--
  The card is a wrapper, and everything it says is one real button inside it. The assignee is a
  control rather than a caption, so it sits beside that button rather than inside it — a button
  nested in a button is neither valid nor navigable — and is placed over the corner the head keeps
  clear for it.
-->
<div
  class="card"
  class:selected
  class:dragging={drag.isDragging(issue.id)}
  data-issue-id={issue.id}>
  <button
    type="button"
    class="main"
    onclick={() => onselect?.(issue)}
    ondblclick={() => onopen?.(issue)}
    onkeydown={(event) => {
      if (event.key === 'Enter') {
        event.preventDefault();
        onopen?.(issue);
      }
    }}
    onpointerdown={(event) => onpress?.(event, issue)}>
    <div class="head">
      <PriorityIcon priority={issue.priority} size={13} />
      <span class="issue-key">{issue.key}</span>
      {#if project}
        <ProjectChip {project} />
      {/if}
    </div>

    <p class="title">{issue.title}</p>

    {#if hasFooter}
      <div class="foot">
        {#each issue.labels.slice(0, 2) as label (label.id)}
          <LabelChip {label} />
        {/each}
        {#if issue.labels.length > 2}<span class="more">+{issue.labels.length - 2}</span>{/if}

        <span class="spacer"></span>

        {#if issue.subIssueCount > 0}
          <span class="meta" title="{issue.subIssueCount} sub-issues">
            <Icon name="corner-down-right" size={11} />{issue.subIssueCount}
          </span>
        {/if}
        {#if issue.commentCount > 0}
          <span class="meta" title="{issue.commentCount} comments">
            <Icon name="message-square" size={11} />{issue.commentCount}
          </span>
        {/if}
        {#if issue.dueDate}
          <span class="meta" class:overdue title={overdue ? 'Overdue' : 'Due'}>
            <Icon name="calendar" size={11} />{formatDate(issue.dueDate)}
          </span>
        {/if}
        {#if issue.estimate}
          <span class="estimate" title="{issue.estimate} points">{issue.estimate}</span>
        {/if}
      </div>
    {/if}
  </button>

  <span class="assignee">
    <AssigneePicker {issue} size={18} />
  </span>
</div>

<style>
  .card {
    position: relative;
    width: 100%;
    padding: var(--s-3) var(--s-4);
    border: 1px solid var(--border);
    border-radius: var(--radius-sm);
    background: var(--bg-surface);
    color: var(--fg);
    cursor: default;
    transition:
      border-color var(--duration) var(--ease),
      box-shadow var(--duration) var(--ease);
  }

  /* Everything the card says, which is the whole of it bar the assignee in the corner. */
  .main {
    display: flex;
    flex-direction: column;
    gap: var(--s-2);
    width: 100%;
    padding: 0;
    border: 0;
    background: none;
    color: inherit;
    text-align: left;
    cursor: default;
  }

  .assignee {
    position: absolute;
    top: var(--s-3);
    right: var(--s-4);
    display: flex;
  }

  .card:hover {
    border-color: var(--border-strong);
    box-shadow: var(--shadow-sm);
  }

  .card.selected {
    border-color: var(--accent);
    box-shadow: 0 0 0 1px var(--accent);
  }

  .card.dragging {
    opacity: 0.4;
  }

  .head,
  .foot {
    display: flex;
    align-items: center;
    gap: var(--s-2);
  }

  /* The corner the assignee is placed over, kept clear so a long project name stops short of it. */
  .head {
    padding-right: calc(18px + var(--s-2));
  }

  /* The key holds its width; the project name beside it is what gives way when the card is narrow. */
  .head .issue-key {
    flex: none;
  }

  .title {
    font-size: var(--text-base);
    line-height: var(--leading-tight);

    /* Two lines, then an ellipsis: a column of cards has to stay scannable. */
    display: -webkit-box;
    -webkit-box-orient: vertical;
    -webkit-line-clamp: 2;
    line-clamp: 2;
    overflow: hidden;
  }

  .meta,
  .more {
    display: inline-flex;
    align-items: center;
    gap: 2px;
    color: var(--fg-tertiary);
    font-size: var(--text-xs);
    font-variant-numeric: tabular-nums;
  }

  .meta.overdue {
    color: var(--danger);
  }

  .estimate {
    display: inline-flex;
    align-items: center;
    justify-content: center;
    min-width: 17px;
    height: 16px;
    border-radius: var(--radius-xs);
    background: var(--bg-sunken);
    color: var(--fg-secondary);
    font-size: var(--text-xs);
  }
</style>
