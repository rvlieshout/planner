<script lang="ts">
  import type { ProjectProgress } from '$lib/api/types';

  /**
   * A project's or milestone's rollup, counted from issues at read time.
   *
   * The bar shows the same thing `ratio` does — completed over non-canceled work — with started work
   * drawn as a lighter segment beside it, because "half done and nothing in flight" and "half done
   * with the rest in progress" are two different states of a project.
   */
  interface Props {
    progress: ProjectProgress;
    /** Off in a dense row, where the numbers beside it would not fit. */
    showCounts?: boolean;
  }

  let { progress, showCounts = true }: Props = $props();

  const scope = $derived(Math.max(progress.total - progress.canceled, 0));
  const done = $derived(scope === 0 ? 0 : (progress.completed / scope) * 100);
  const started = $derived(scope === 0 ? 0 : (progress.started / scope) * 100);
</script>

<div class="progress">
  <div
    class="track"
    role="progressbar"
    aria-valuenow={Math.round(progress.ratio * 100)}
    aria-valuemin="0"
    aria-valuemax="100"
    aria-label="Completed">
    <span class="done" style:width="{done}%"></span>
    <span class="started" style:width="{started}%"></span>
  </div>

  {#if showCounts}
    <span class="counts">
      <strong>{Math.round(progress.ratio * 100)}%</strong>
      <span class="muted">
        {progress.completed} of {scope} done{#if progress.started}, {progress.started} in progress{/if}{#if progress.canceled}, {progress.canceled} canceled{/if}
      </span>
    </span>
  {/if}
</div>

<style>
  .progress {
    display: flex;
    align-items: center;
    gap: var(--s-4);
  }

  .track {
    display: flex;
    flex: 1;
    max-width: 420px;
    height: 6px;
    overflow: hidden;
    border-radius: var(--radius-full);
    background: var(--bg-sunken);
  }

  .done {
    background: var(--success);
  }

  .started {
    background: var(--accent);
    opacity: 0.45;
  }

  .counts {
    display: flex;
    gap: var(--s-3);
    align-items: baseline;
    font-size: var(--text-sm);
    font-variant-numeric: tabular-nums;
    white-space: nowrap;
  }
</style>
