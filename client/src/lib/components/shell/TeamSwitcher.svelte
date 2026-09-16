<script lang="ts">
  import Icon from '$components/Icon.svelte';
  import Popover from '$components/Popover.svelte';
  import { workspace } from '$lib/workspace.svelte';
  import { mayDiscard } from '$lib/navigation.svelte';
  import { readableOn } from '$lib/format';

  /**
   * Every team the caller can read. Switching rebuilds the rest of the sidebar.
   *
   * It asks before switching if the open page is dirty, and stays where it was when the answer is no
   * — a switcher that moves anyway is one that has already thrown the work away.
   */
  let open = $state(false);

  const current = $derived(workspace.currentTeam);
  const teams = $derived(
    [...workspace.teams].sort(
      (a, b) => Number(Boolean(a.archivedAt)) - Number(Boolean(b.archivedAt)) || a.name.localeCompare(b.name)
    )
  );

  async function choose(teamId: string) {
    open = false;

    if (teamId === workspace.currentTeamId) return;
    if (!(await mayDiscard())) return;

    await workspace.setTeam(teamId);
  }
</script>

<div class="switcher">
  <Popover {open} onclose={() => (open = false)} width="trigger">
    {#snippet trigger()}
      <button
        type="button"
        class="trigger"
        onclick={() => (open = !open)}
        aria-haspopup="listbox"
        aria-expanded={open}>
        {#if current}
          <span class="key" style:background={current.color} style:color={readableOn(current.color)}>
            {current.key}
          </span>
          <span class="truncate name">{current.name}</span>
        {:else}
          <span class="key empty"><Icon name="users" size={12} /></span>
          <span class="truncate name muted">No team</span>
        {/if}
        <Icon name="chevron-down" size={12} />
      </button>
    {/snippet}

    <div class="menu" role="listbox">
      {#each teams as team (team.id)}
        <button
          type="button"
          class="option"
          class:selected={team.id === workspace.currentTeamId}
          role="option"
          aria-selected={team.id === workspace.currentTeamId}
          onclick={() => choose(team.id)}>
          <span class="key small" style:background={team.color} style:color={readableOn(team.color)}>
            {team.key}
          </span>
          <span class="truncate">{team.name}</span>
          {#if team.archivedAt}<span class="chip">Archived</span>{/if}
          {#if team.id === workspace.currentTeamId}<Icon name="check" size={13} class="tick" />{/if}
        </button>
      {:else}
        <p class="none">You are not in any team yet.</p>
      {/each}
    </div>
  </Popover>
</div>

<style>
  .switcher {
    padding: var(--s-3);
    border-bottom: 1px solid var(--border);
  }

  .trigger {
    display: flex;
    align-items: center;
    gap: var(--s-3);
    width: 100%;
    height: 32px;
    padding: 0 var(--s-3);
    border: 1px solid transparent;
    border-radius: var(--radius-sm);
    background: none;
    color: var(--fg-tertiary);
    cursor: pointer;
  }

  .trigger:hover {
    background: var(--bg-hover);
  }

  .key {
    display: grid;
    flex: none;
    place-items: center;
    min-width: 24px;
    height: 20px;
    padding: 0 var(--s-2);
    border-radius: var(--radius-xs);
    font-family: var(--font-mono);
    font-size: 10px;
    font-weight: 500;
    letter-spacing: 0.02em;
  }

  .key.small {
    height: 18px;
    font-size: 9px;
  }

  .key.empty {
    background: var(--bg-active);
    color: var(--fg-tertiary);
  }

  .name {
    flex: 1;
    color: var(--fg);
    font-size: var(--text-base);
    font-weight: 500;
    text-align: left;
  }

  .menu {
    min-width: 220px;
  }

  .option {
    display: flex;
    align-items: center;
    gap: var(--s-3);
    width: 100%;
    height: var(--row-h);
    padding: 0 var(--s-3);
    border: 0;
    border-radius: var(--radius-xs);
    background: none;
    text-align: left;
    cursor: pointer;
  }

  .option:hover {
    background: var(--bg-hover);
  }

  .option.selected {
    color: var(--accent);
  }

  .option :global(.tick) {
    margin-left: auto;
  }

  .none {
    padding: var(--s-4);
    color: var(--fg-tertiary);
    font-size: var(--text-sm);
  }
</style>
