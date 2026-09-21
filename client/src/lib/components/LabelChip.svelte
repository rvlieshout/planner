<script lang="ts">
  import type { LabelDto } from '$lib/api/types';

  /**
   * A label keeps its own colour everywhere it appears, but only in its dot. The chip itself is a
   * neutral outline: a row of tinted chips competes with the title it sits next to, and the dot is
   * enough to tell one label from another at a glance.
   */
  interface Props {
    label: LabelDto;
    /** Just the dot, for a dense board card where the name would not fit anyway. */
    compact?: boolean;
  }

  let { label, compact = false }: Props = $props();
</script>

{#if compact}
  <span class="dot" style:background={label.color} title={label.name}></span>
{:else}
  <span class="label-chip" title={label.description ?? label.name}>
    <span class="dot" style:background={label.color}></span>
    <span class="truncate">{label.name}</span>
  </span>
{/if}

<style>
  .label-chip {
    display: inline-flex;
    align-items: center;
    gap: var(--s-2);
    max-width: 180px;
    height: 18px;
    padding: 0 var(--s-2) 0 var(--s-3);
    border: 1px solid var(--border);
    border-radius: var(--radius-full);
    color: var(--fg-tertiary);
    font-size: var(--text-xs);
    white-space: nowrap;
  }

  .dot {
    flex: none;
    width: 6px;
    height: 6px;
    border-radius: 50%;
  }
</style>
