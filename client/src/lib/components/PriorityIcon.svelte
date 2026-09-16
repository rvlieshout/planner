<script lang="ts">
  import Icon from './Icon.svelte';
  import { PRIORITY } from '$lib/meta';
  import type { IssuePriority } from '$lib/api/types';

  interface Props {
    priority: IssuePriority;
    size?: number;
    /** Off in a dense row, where the column position already says what the glyph means. */
    showLabel?: boolean;
  }

  let { priority, size = 14, showLabel = false }: Props = $props();

  const meta = $derived(PRIORITY[priority] ?? PRIORITY.None);
</script>

<span class="priority" style:color={meta.color} title={meta.label}>
  <Icon name={meta.icon} {size} label={showLabel ? undefined : meta.label} />
  {#if showLabel}<span class="text">{meta.label}</span>{/if}
</span>

<style>
  .priority {
    display: inline-flex;
    align-items: center;
    gap: var(--s-2);
  }

  .text {
    color: var(--fg);
  }
</style>
