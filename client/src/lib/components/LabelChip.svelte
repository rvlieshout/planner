<script lang="ts">
  import { alpha } from '$lib/format';
  import type { LabelDto } from '$lib/api/types';

  /**
   * A label keeps its own colour everywhere it appears — as a dot and a tint rather than a solid
   * fill, because a row of six saturated chips is a row nobody can read a title through.
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
  <span
    class="label-chip"
    style:background={alpha(label.color, 0.13)}
    style:border-color={alpha(label.color, 0.35)}
    title={label.description ?? label.name}>
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
    height: 20px;
    padding: 0 var(--s-3);
    border: 1px solid transparent;
    border-radius: var(--radius-full);
    color: var(--fg-secondary);
    font-size: var(--text-xs);
    white-space: nowrap;
  }

  .dot {
    flex: none;
    width: 7px;
    height: 7px;
    border-radius: 50%;
  }
</style>
