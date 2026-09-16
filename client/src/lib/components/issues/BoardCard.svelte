<script lang="ts">
  import Avatar from '$components/Avatar.svelte';
  import Icon from '$components/Icon.svelte';
  import LabelChip from '$components/LabelChip.svelte';
  import PriorityIcon from '$components/PriorityIcon.svelte';
  import { drag } from '$lib/dnd.svelte';
  import { formatDate, isOverdue } from '$lib/format';
  import type { IssueSummary } from '$lib/api/types';

  /**
   * One issue on a board.
   *
   * Three rows at most: the key and its priority, the title, and whatever else is actually set. A
   * card with placeholders for everything an issue *could* have is a card you have to read rather
   * than scan.
   */
  interface Props {
    issue: IssueSummary;
    selected?: boolean;
    onselect?: (issue: IssueSummary) => void;
    onopen?: (issue: IssueSummary) => void;
    onpress?: (event: PointerEvent, issue: IssueSummary) => void;
  }

  let { issue, selected = false, onselect, onopen, onpress }: Props = $props();

  const overdue = $derived(isOverdue(issue.dueDate));
  const hasFooter = $derived(
    issue.labels.length > 0 ||
      Boolean(issue.dueDate) ||
      Boolean(issue.estimate) ||
      issue.subIssueCount > 0 ||
      issue.commentCount > 0 ||
      Boolean(issue.assignee)
  );
</script>

<button
  type="button"
  class="card"
  class:selected
  class:dragging={drag.isDragging(issue.id)}
  data-issue-id={issue.id}
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
    <span class="spacer"></span>
    {#if issue.assignee}
      <Avatar name={issue.assignee.displayName} seed={issue.assignee.email} size={18} />
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

<style>
  .card {
    display: flex;
    flex-direction: column;
    gap: var(--s-2);
    width: 100%;
    padding: var(--s-3) var(--s-4);
    border: 1px solid var(--border);
    border-radius: var(--radius-sm);
    background: var(--bg-surface);
    color: var(--fg);
    text-align: left;
    cursor: default;
    transition:
      border-color var(--duration) var(--ease),
      box-shadow var(--duration) var(--ease);
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
