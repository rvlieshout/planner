<script lang="ts">
  import AssigneePicker from './AssigneePicker.svelte';
  import PriorityPicker from './PriorityPicker.svelte';
  import Icon from '$components/Icon.svelte';
  import LabelChip from '$components/LabelChip.svelte';
  import ProjectChip from '$components/ProjectChip.svelte';
  import StateIcon from '$components/StateIcon.svelte';
  import { formatDate, formatExact, isOverdue, relativeTime } from '$lib/format';
  import { drag } from '$lib/dnd.svelte';
  import type { IssueSummary, ProjectDto } from '$lib/api/types';

  /**
   * The dense list row: priority, key, status, title, labels, and when it last moved.
   *
   * One row per line at 28px, because a tracker is read by scanning a column of them — the moment a
   * row wraps, the eye has to parse each one instead of sweeping past it. Everything that would wrap
   * is truncated or dropped at narrow widths instead.
   */
  interface Props {
    issue: IssueSummary;
    /** The issue's project, named after its title — left out on a list that is already one project. */
    project?: ProjectDto | null;
    selected?: boolean;
    /** Off for a static list — sub-issues on a detail page are not reordered by dragging. */
    draggable?: boolean;
    onselect?: (issue: IssueSummary) => void;
    onopen?: (issue: IssueSummary) => void;
    onpress?: (event: PointerEvent, issue: IssueSummary) => void;
  }

  let {
    issue,
    project = null,
    selected = false,
    draggable = false,
    onselect,
    onopen,
    onpress
  }: Props = $props();

  const overdue = $derived(isOverdue(issue.dueDate));

  function onKeyDown(event: KeyboardEvent) {
    if (event.key === 'Enter') {
      event.preventDefault();
      onopen?.(issue);
    }
  }
</script>

<!--
  The row is a wrapper, and everything but the assignee is one real button inside it — Enter, focus
  order and the accessibility tree all come free from that, and the press is remembered rather than
  handled, which is what lets a click still select and a double-click still open while a drag starts
  from the same gesture.

  The priority and the assignee are controls of their own, so they are siblings of that button rather
  than buttons nested inside it: the row keeps its single Enter target, the picker keeps its own, and neither has to
  swallow the other's clicks to stay out of its way.
-->
<div
  class="row"
  class:selected
  class:dragging={drag.isDragging(issue.id)}
  data-issue-id={issue.id}>
  <PriorityPicker {issue} size={13} />

  <button
    type="button"
    class="main"
    onclick={() => onselect?.(issue)}
    ondblclick={() => onopen?.(issue)}
    onkeydown={onKeyDown}
    onpointerdown={draggable ? (event) => onpress?.(event, issue) : undefined}>
    <span class="issue-key">{issue.key}</span>

    <StateIcon type={issue.stateType} color={issue.stateColor} title={issue.stateName} size={13} />

    <span class="title truncate">{issue.title}</span>

    {#if project}
      <span class="project"><ProjectChip {project} /></span>
    {/if}

    {#if issue.subIssueCount > 0}
      <span class="meta" title="{issue.subIssueCount} sub-issues">
        <Icon name="corner-down-right" size={12} />
        {issue.subIssueCount}
      </span>
    {/if}

    {#if issue.commentCount > 0}
      <span class="meta" title="{issue.commentCount} comments">
        <Icon name="message-square" size={12} />
        {issue.commentCount}
      </span>
    {/if}

    <span class="labels">
      {#each issue.labels.slice(0, 3) as label (label.id)}
        <LabelChip {label} />
      {/each}
      {#if issue.labels.length > 3}
        <span class="more">+{issue.labels.length - 3}</span>
      {/if}
    </span>

    {#if issue.dueDate}
      <span class="due" class:overdue title={overdue ? 'Overdue' : 'Due'}>
        <Icon name="calendar" size={12} />
        {formatDate(issue.dueDate)}
      </span>
    {/if}

    {#if issue.estimate}
      <span class="estimate" title="{issue.estimate} points">{issue.estimate}</span>
    {/if}

    <span class="updated" title={formatExact(issue.updatedAt)}>{relativeTime(issue.updatedAt)}</span>
  </button>

  <AssigneePicker {issue} size={18} />
</div>

<style>
  .row {
    display: flex;
    align-items: center;
    gap: var(--s-3);
    width: 100%;
    height: var(--row-h);
    padding: 0 var(--s-5);
    border-left: 2px solid transparent;
    color: var(--fg);
    font-size: var(--text-base);
    cursor: default;
  }

  /* The row's whole width bar the priority and the assignee, so a click anywhere across it selects. */
  .main {
    display: flex;
    flex: 1;
    align-items: center;
    gap: var(--s-3);
    min-width: 0;
    height: 100%;
    padding: 0;
    border: 0;
    background: none;
    color: inherit;
    font-size: inherit;
    text-align: left;
    cursor: default;
  }

  .row:hover {
    background: var(--bg-hover);
  }

  .row.selected {
    border-left-color: var(--accent);
    background: var(--bg-selected);
  }

  /* Faded, not hidden: which row is in flight has to stay visible to make sense of the ghost. */
  .row.dragging {
    opacity: 0.4;
  }

  .issue-key {
    flex: none;
    width: 72px;
  }

  .title {
    flex: 1;
    min-width: 0;
  }

  .meta,
  .due,
  .estimate,
  .updated {
    display: inline-flex;
    flex: none;
    align-items: center;
    gap: 3px;
    color: var(--fg-tertiary);
    font-size: var(--text-xs);
    font-variant-numeric: tabular-nums;
  }

  .due.overdue {
    color: var(--danger);
  }

  .estimate {
    justify-content: center;
    min-width: 18px;
    height: 16px;
    border-radius: var(--radius-xs);
    background: var(--bg-sunken);
  }

  .updated {
    width: 100px;
    justify-content: flex-end;
    text-overflow: ellipsis;
    white-space: nowrap;
    overflow: hidden;
  }

  .project {
    display: inline-flex;
    flex: none;
    align-items: center;
  }

  .labels {
    display: flex;
    flex: none;
    gap: var(--s-2);
    align-items: center;
  }

  .more {
    color: var(--fg-tertiary);
    font-size: var(--text-xs);
  }

  /* Narrow windows drop the decoration rather than wrapping the row. */
  @media (width <= 1100px) {
    .labels,
    .due,
    .meta,
    .project {
      display: none;
    }
  }

  @media (width <= 760px) {
    .updated,
    .estimate {
      display: none;
    }

    .issue-key {
      width: 60px;
    }
  }
</style>
