<script lang="ts">
  import { page } from '$app/state';
  import { resolve } from '$app/paths';
  import Icon from '$components/Icon.svelte';
  import Avatar from '$components/Avatar.svelte';
  import Popover from '$components/Popover.svelte';
  import TeamSwitcher from './TeamSwitcher.svelte';
  import { session } from '$lib/auth/session.svelte';
  import { workspace } from '$lib/workspace.svelte';
  import { compareRank } from '$lib/rank';
  import { navigate } from '$lib/navigation.svelte';
  import { describe } from '$lib/shortcuts';
  import { realtime } from '$lib/realtime/hub.svelte';
  import type { Pathname } from '$app/types';

  /**
   * The application's map.
   *
   * My Issues sits at the top because it is the one view about the person rather than the team, and
   * it is the question someone opens a tracker to answer. Administration sits above it, offered only
   * to the people entitled to it, and the projects of the current team fill the rest.
   */
  interface Props {
    onSignOut: () => void;
  }

  let { onSignOut }: Props = $props();

  let accountOpen = $state(false);

  const path = $derived(page.url.pathname);
  const teamId = $derived(workspace.currentTeamId);
  const projects = $derived(
    [...workspace.projects]
      .filter((project) => !project.archivedAt)
      .sort((a, b) => compareRank(a.rank, b.rank) || a.name.localeCompare(b.name))
  );

  const isActive = (href: Pathname, exact = true) =>
    exact ? path === resolve(href) : path.startsWith(resolve(href));

  async function go(href: Pathname) {
    await navigate(href);
  }
</script>

<nav class="sidebar" aria-label="Main">
  <TeamSwitcher />

  <div class="scroll">
    {#if session.canOpenUserAdmin || session.canOpenTeamAdmin}
      <section>
        <p class="caption section-label">Administration</p>

        {#if session.canOpenUserAdmin}
          <button
            type="button"
            class="row"
            class:active={isActive('/users', false)}
            onclick={() => go('/users')}>
            <Icon name="users" size={15} />
            <span class="truncate">Users &amp; access</span>
          </button>
        {/if}

        {#if session.canOpenTeamAdmin}
          <button
            type="button"
            class="row"
            class:active={isActive('/teams', false)}
            onclick={() => go('/teams')}>
            <Icon name="shield" size={15} />
            <span class="truncate">Teams</span>
          </button>
        {/if}
      </section>
    {/if}

    <section>
      <button
        type="button"
        class="row"
        class:active={isActive('/my-issues')}
        onclick={() => go('/my-issues')}>
        <Icon name="circle-user" size={15} />
        <span class="truncate">My Issues</span>
      </button>

      {#if teamId}
        <button type="button" class="row" class:active={isActive('/board')} onclick={() => go('/board')}>
          <Icon name="layout-grid" size={15} />
          <span class="truncate">{workspace.currentTeam?.name ?? 'Board'}</span>
        </button>
      {/if}
    </section>

    {#if teamId}
      <section>
        <div class="section-head">
          <p class="caption section-label">Projects</p>
          <button
            type="button"
            class="add"
            onclick={() => go('/projects/new')}
            title="New project ({describe('shift+p')})"
            aria-label="New project">
            <Icon name="plus" size={13} />
          </button>
        </div>

        {#each projects as project (project.id)}
          <button
            type="button"
            class="row"
            class:active={isActive(`/projects/${project.id}`)}
            onclick={() => go(`/projects/${project.id}`)}>
            <span class="project-dot" style:background={project.color}></span>
            <span class="truncate">{project.name}</span>
            {#if project.progress.total > 0}
              <span class="count">{project.progress.completed}/{project.progress.total}</span>
            {/if}
          </button>
        {:else}
          <p class="hint">No projects yet.</p>
        {/each}
      </section>
    {/if}
  </div>

  <footer>
    <Popover open={accountOpen} onclose={() => (accountOpen = false)} align="start" width={220}>
      {#snippet trigger()}
        <button type="button" class="account" onclick={() => (accountOpen = !accountOpen)}>
          <Avatar
            name={session.user?.displayName}
            src={session.user?.avatarUrl}
            seed={session.user?.email}
            size={22} />
          <span class="who">
            <span class="truncate name">{session.user?.displayName ?? ''}</span>
            <span class="truncate email">{session.user?.email ?? ''}</span>
          </span>
          <Icon name="chevron-down" size={12} />
        </button>
      {/snippet}

      <div class="menu">
        <button
          type="button"
          class="menu-item"
          onclick={() => {
            accountOpen = false;
            void go('/settings');
          }}>
          <Icon name="settings" size={14} />
          <span>Preferences</span>
        </button>
        <hr />
        <button
          type="button"
          class="menu-item danger"
          onclick={() => {
            accountOpen = false;
            onSignOut();
          }}>
          <Icon name="log-out" size={14} />
          <span>Sign out</span>
        </button>
      </div>
    </Popover>

    <span class="live" class:on={realtime.isConnected} title={realtime.isConnected ? 'Live' : 'Offline'}>
    </span>
  </footer>
</nav>

<style>
  .sidebar {
    display: flex;
    flex-direction: column;
    height: 100%;
    border-right: 1px solid var(--border);
    background: var(--bg-app);
    overflow: hidden;
  }

  .scroll {
    flex: 1;
    min-height: 0;
    padding: var(--s-3) var(--s-3) var(--s-6);
    overflow-y: auto;
  }

  section + section {
    margin-top: var(--s-5);
  }

  .section-label {
    padding: 0 var(--s-3);
  }

  .section-head {
    display: flex;
    align-items: center;
    justify-content: space-between;
    height: var(--row-h-sm);
  }

  .add {
    display: grid;
    place-items: center;
    width: 20px;
    height: 20px;
    border: 0;
    border-radius: var(--radius-xs);
    background: none;
    color: var(--fg-tertiary);
    cursor: pointer;
  }

  .add:hover {
    background: var(--bg-active);
    color: var(--fg);
  }

  .row {
    display: flex;
    align-items: center;
    gap: var(--s-3);
    width: 100%;
    height: var(--row-h);
    padding: 0 var(--s-3);
    border: 0;
    border-radius: var(--radius-sm);
    background: none;
    color: var(--fg-secondary);
    font-size: var(--text-base);
    text-align: left;
    cursor: pointer;
  }

  .row:hover {
    background: var(--bg-hover);
    color: var(--fg);
  }

  .row.active {
    background: var(--bg-selected);
    color: var(--accent);
    font-weight: 500;
  }

  .project-dot {
    flex: none;
    width: 9px;
    height: 9px;
    margin: 0 3px;
    border-radius: 3px;
  }

  .count {
    margin-left: auto;
    color: var(--fg-tertiary);
    font-size: var(--text-xs);
    font-variant-numeric: tabular-nums;
  }

  .hint {
    padding: var(--s-2) var(--s-3);
    color: var(--fg-tertiary);
    font-size: var(--text-sm);
  }

  footer {
    display: flex;
    align-items: center;
    gap: var(--s-3);
    padding: var(--s-3);
    border-top: 1px solid var(--border);
  }

  .account {
    display: flex;
    flex: 1;
    align-items: center;
    gap: var(--s-3);
    min-width: 0;
    padding: var(--s-2);
    border: 0;
    border-radius: var(--radius-sm);
    background: none;
    color: var(--fg-tertiary);
    cursor: pointer;
  }

  .account:hover {
    background: var(--bg-hover);
  }

  .who {
    display: flex;
    flex: 1;
    flex-direction: column;
    min-width: 0;
    text-align: left;
  }

  .name {
    color: var(--fg);
    font-size: var(--text-sm);
    font-weight: 500;
  }

  .email {
    font-size: var(--text-xs);
  }

  .live {
    flex: none;
    width: 7px;
    height: 7px;
    margin-right: var(--s-2);
    border-radius: 50%;
    background: var(--fg-disabled);
  }

  .live.on {
    background: var(--success);
  }

  .menu {
    min-width: 200px;
  }

  .menu-item {
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

  .menu-item:hover {
    background: var(--bg-hover);
  }

  .menu-item.danger:hover {
    background: var(--danger-bg);
    color: var(--danger);
  }

  hr {
    margin: var(--s-2) 0;
  }
</style>
