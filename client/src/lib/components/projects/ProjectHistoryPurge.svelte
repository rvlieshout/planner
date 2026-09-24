<script lang="ts">
  import { activity as activityApi, ApiError } from '$lib/api';
  import type { Guid } from '$lib/api/types';
  import { plural } from '$lib/format';
  import Icon from '$components/Icon.svelte';
  import Select from '$components/Select.svelte';
  import type { SelectOption } from '$components/select';
  import { confirm } from '$components/confirm.svelte';
  import { toasts } from '$components/toast.svelte';

  /**
   * Clearing a project's activity history — all of it, or what is older than a number of days.
   *
   * Administrators only; the API enforces that, and the editor only draws this for them. The inbox
   * entries that pointed at the deleted events go with them. The purge itself is recorded, so the
   * history always says who cleared it and how much.
   */
  interface Props {
    projectId: Guid;
    projectName: string;
  }

  let { projectId, projectName }: Props = $props();

  const ALL = 0;
  const CUSTOM = -1;

  let range = $state<number>(90);
  let customDays = $state(30);
  let purging = $state(false);

  const options: SelectOption<number>[] = [
    { value: 30, label: 'Older than 30 days', icon: 'clock' },
    { value: 90, label: 'Older than 90 days', icon: 'clock' },
    { value: 180, label: 'Older than 180 days', icon: 'clock' },
    { value: 365, label: 'Older than a year', icon: 'clock' },
    { value: CUSTOM, label: 'Older than…', icon: 'calendar' },
    { value: ALL, label: 'All history', icon: 'trash-2', color: 'var(--danger)' }
  ];

  const days = $derived(range === CUSTOM ? Math.floor(customDays) : range);
  const valid = $derived(range === ALL || (Number.isFinite(days) && days >= 1 && days <= 36_500));
  const describe = $derived(range === ALL ? 'all of its activity history' : `activity older than ${plural(days, 'day')}`);

  async function purge() {
    if (!valid || purging) return;

    const answer = await confirm.ask({
      title: range === ALL ? `Delete all history of “${projectName}”?` : 'Delete old history?',
      message:
        `This permanently deletes ${describe} in “${projectName}”, including the inbox entries about it. ` +
        'Issues, comments and files are not touched. This cannot be undone.',
      // Wiping everything is the one that deserves the extra step.
      requiredText: range === ALL ? projectName : undefined,
      confirmLabel: 'Delete history',
      cancelLabel: 'Cancel',
      danger: true
    });

    if (!answer) return;

    purging = true;

    try {
      const result = await activityApi.purge({ projectId, olderThanDays: range === ALL ? undefined : days });
      toasts.success(result.deleted === 0 ? 'There was nothing to delete.' : `Deleted ${plural(result.deleted, 'entry', 'entries')}.`);
    } catch (failure) {
      toasts.error(failure instanceof ApiError ? failure.message : 'Deleting the history failed.');
    } finally {
      purging = false;
    }
  }
</script>

<section class="panel">
  <div class="panel-title"><span>Activity history</span></div>
  <p class="muted small">
    Delete this project's activity feed entries, and the inbox entries that point at them. Only
    administrators can do this, and the deletion itself is recorded.
  </p>

  <div class="controls">
    <Select {options} value={range} onchange={(value) => (range = value)} label="What to delete" />

    {#if range === CUSTOM}
      <label class="days">
        <input class="input" type="number" min="1" max="36500" step="1" bind:value={customDays} aria-label="Days" />
        <span class="muted">days</span>
      </label>
    {/if}

    <button type="button" class="btn btn-danger" disabled={!valid || purging} onclick={() => void purge()}>
      <Icon name={purging ? 'loader-circle' : 'trash-2'} size={14} class={purging ? 'spin' : ''} />
      {purging ? 'Deleting…' : 'Delete history'}
    </button>
  </div>
</section>

<style>
  .controls {
    display: flex;
    flex-wrap: wrap;
    align-items: center;
    gap: var(--s-3);
  }

  .days {
    display: inline-flex;
    align-items: center;
    gap: var(--s-2);
  }

  .days .input {
    width: 90px;
  }

  .small {
    font-size: var(--text-sm);
  }
</style>
