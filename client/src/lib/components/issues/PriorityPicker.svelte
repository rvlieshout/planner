<script lang="ts">
  import Icon from '$components/Icon.svelte';
  import PriorityIcon from '$components/PriorityIcon.svelte';
  import Select from '$components/Select.svelte';
  import type { SelectOption } from '$components/select';
  import { Permission, session } from '$lib/auth/session.svelte';
  import { prioritizeIssue } from '$lib/issues/priority';
  import { PRIORITY, PRIORITY_ORDER } from '$lib/meta';
  import type { IssuePriority, IssueSummary } from '$lib/api/types';

  /**
   * The priority on a row or a card: the same glyph as before, but a control.
   *
   * The sibling of `AssigneePicker`, for the same reason — triaging a column is a matter of changing
   * what is urgent, and that should cost one click rather than a trip through the issue. Someone who
   * cannot write to the team gets the plain glyph.
   */
  interface Props {
    issue: IssueSummary;
    size?: number;
  }

  let { issue, size = 13 }: Props = $props();

  const canWrite = $derived(session.can(issue.teamId, Permission.Write));
  const meta = $derived(PRIORITY[issue.priority] ?? PRIORITY.None);

  const options = $derived<SelectOption<IssuePriority>[]>(
    PRIORITY_ORDER.map((priority) => ({
      value: priority,
      label: PRIORITY[priority].label,
      icon: PRIORITY[priority].icon,
      color: PRIORITY[priority].color
    }))
  );
</script>

{#if canWrite}
  <Select
    {options}
    value={issue.priority}
    onchange={(priority) => void prioritizeIssue(issue, priority)}
    label="Priority">
    {#snippet trigger({ open, toggle, onkeydown })}
      <button
        type="button"
        class="glyph"
        class:open
        style:color={meta.color}
        style:--size="{size + 6}px"
        aria-haspopup="listbox"
        aria-expanded={open}
        aria-label="Priority: {meta.label}"
        title="{meta.label} — click to change"
        onclick={toggle}
        {onkeydown}>
        <Icon name={meta.icon} {size} />
      </button>
    {/snippet}
  </Select>
{:else}
  <PriorityIcon priority={issue.priority} {size} />
{/if}

<style>
  .glyph {
    display: grid;
    flex: none;
    place-items: center;
    width: var(--size);
    height: var(--size);
    /* Pulled back over its own padding, so the glyph stands where the plain icon stood. */
    margin: -3px;
    padding: 0;
    border: 0;
    border-radius: var(--radius-xs);
    background: none;
    cursor: pointer;
  }

  .glyph:hover,
  .glyph.open {
    background: var(--bg-active);
  }
</style>
