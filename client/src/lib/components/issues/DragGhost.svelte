<script lang="ts">
  import { drag } from '$lib/dnd.svelte';
  import PriorityIcon from '$components/PriorityIcon.svelte';

  /**
   * The card that follows the cursor.
   *
   * It exists because a drag owns the pointer: the only thing an application can put underneath one
   * is something it draws itself. Fixed, above everything, and outside every clip, so it keeps up
   * even over the gaps between columns where no column would raise an event.
   */
  const issue = $derived(drag.issue);
</script>

{#if issue}
  <div class="ghost" style:left="{drag.x}px" style:top="{drag.y}px" aria-hidden="true">
    <PriorityIcon priority={issue.priority} size={13} />
    <span class="issue-key">{issue.key}</span>
    <span class="truncate">{issue.title}</span>
  </div>
{/if}

<style>
  .ghost {
    position: fixed;
    z-index: var(--z-drag);
    display: flex;
    align-items: center;
    gap: var(--s-3);
    max-width: 320px;
    padding: var(--s-3) var(--s-4);
    border: 1px solid var(--accent-border);
    border-radius: var(--radius-sm);
    background: var(--bg-raised);
    box-shadow: var(--shadow-drag);
    font-size: var(--text-sm);
    pointer-events: none;

    /* Held just below and right of the cursor, where a dragged thing sits on every desktop. */
    transform: translate(10px, 10px);
  }
</style>
