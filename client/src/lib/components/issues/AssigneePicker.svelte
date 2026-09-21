<script lang="ts">
  import Avatar from '$components/Avatar.svelte';
  import Icon from '$components/Icon.svelte';
  import Select from '$components/Select.svelte';
  import type { SelectOption } from '$components/select';
  import { Permission, session } from '$lib/auth/session.svelte';
  import { assignIssue } from '$lib/issues/assign';
  import { workspace } from '$lib/workspace.svelte';
  import type { Guid, IssueSummary } from '$lib/api/types';

  /**
   * The assignee on a row or a card: the same monogram as before, but a control.
   *
   * A board is where you notice that nobody is on something, which makes it the place where handing
   * it to someone should cost one click rather than a trip through the issue. Empty is *drawn* for
   * the same reason — an unassigned issue is a thing to act on, and a gap where a face would be says
   * nothing at all, so the outline of a person stands in for one.
   *
   * The list, the search and the keys are the property picker's own; only the trigger is different.
   * Members come from the workspace cache, which a team's own board has usually filled already —
   * My Issues spans teams, so the ones it has not are fetched when the picker is opened rather than
   * one request per row on the way in.
   */
  interface Props {
    issue: IssueSummary;
    size?: number;
  }

  let { issue, size = 18 }: Props = $props();

  const NONE = '';

  const canAssign = $derived(session.can(issue.teamId, Permission.Write));
  const assignee = $derived(issue.assignee);
  const members = $derived(workspace.membersNow(issue.teamId));

  const hint = $derived(
    assignee
      ? `${assignee.displayName} — click to reassign`
      : canAssign
        ? 'Unassigned — click to assign'
        : 'Unassigned'
  );

  const options = $derived<SelectOption<Guid | ''>[]>([
    { value: NONE, label: 'Unassigned', icon: 'circle-user', color: 'var(--fg-tertiary)' },
    ...[...members]
      .sort((a, b) => a.displayName.localeCompare(b.displayName))
      .map((member) => ({
        value: member.userId,
        label: member.displayName,
        avatarName: member.displayName,
        avatarSeed: member.email,
        hint: member.email
      }))
  ]);

  function choose(userId: Guid | '') {
    void assignIssue(issue, members.find((member) => member.userId === userId) ?? null);
  }
</script>

{#snippet face()}
  {#if assignee}
    <Avatar name={assignee.displayName} seed={assignee.email} title={hint} {size} />
  {:else}
    <Icon name="circle-user" size={size - 2} />
  {/if}
{/snippet}

{#if canAssign}
  <Select {options} value={assignee?.id ?? NONE} onchange={choose} label="Assignee">
    {#snippet trigger({ open, toggle, onkeydown })}
      <button
        type="button"
        class="face"
        class:open
        class:empty={!assignee}
        style:--size="{size}px"
        aria-haspopup="listbox"
        aria-expanded={open}
        aria-label={assignee ? `Assignee: ${assignee.displayName}` : 'Unassigned'}
        title={hint}
        onclick={() => {
          // Opened, then filled: the cache answers straight away on a board that has already drawn
          // this team, and a team it has not simply gains its members a moment later.
          void workspace.membersFor(issue.teamId);
          toggle();
        }}
        {onkeydown}>
        {@render face()}
      </button>
    {/snippet}
  </Select>
{:else}
  <span class="face" style:--size="{size}px" title={hint}>
    {@render face()}
  </span>
{/if}

<style>
  .face {
    display: grid;
    flex: none;
    place-items: center;
    width: var(--size);
    height: var(--size);
    padding: 0;
    border: 0;
    border-radius: 50%;
    background: none;
    color: var(--fg-disabled);
    cursor: pointer;
  }

  /*
   * A ring rather than a fill: the monogram is already a coloured disc, so the only room left to say
   * "this is a control" is just outside it.
   */
  button.face:hover,
  button.face.open {
    box-shadow: 0 0 0 2px var(--bg-active);
  }

  button.face.empty:hover,
  button.face.empty.open {
    color: var(--fg-secondary);
  }

  span.face {
    cursor: inherit;
  }
</style>
