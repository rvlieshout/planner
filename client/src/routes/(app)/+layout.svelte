<script lang="ts">
  import { page } from '$app/state';
  import { resolve } from '$app/paths';
  import { goto } from '$app/navigation';

  import Icon from '$components/Icon.svelte';
  import ThemeToggle from '$components/ThemeToggle.svelte';
  import Shortcut from '$components/Shortcut.svelte';
  import Sidebar from '$components/shell/Sidebar.svelte';
  import AppMenu from '$components/shell/AppMenu.svelte';
  import CommandPalette from '$components/shell/CommandPalette.svelte';
  import IssueEditorDialog from '$components/issues/IssueEditorDialog.svelte';
  import DragGhost from '$components/issues/DragGhost.svelte';

  import { chrome } from '$lib/chrome.svelte';
  import { session } from '$lib/auth/session.svelte';
  import { settings, SIDEBAR_MAX, SIDEBAR_MIN } from '$lib/settings.svelte';
  import { workspace } from '$lib/workspace.svelte';
  import { realtime } from '$lib/realtime/hub.svelte';
  import { issueEditor } from '$lib/issues/editor.svelte';
  import { installNavigationGuard, mayDiscard, navigate } from '$lib/navigation.svelte';
  import { commands, type CommandGroup } from '$lib/commands.svelte';
  import { describe } from '$lib/shortcuts';
  import { BUILD_LABEL, VERSION } from '$lib/version';
  import { appUpdate } from '$lib/update.svelte';

  /**
   * The window frame: a title bar with the application menu, a sidebar you can drag or collapse, a
   * toolbar strip over the content, and a status bar under it.
   *
   * None of it knows what page is open. The page publishes its heading, its actions and its one-line
   * summary through `chrome`, and this reads them — which is the only arrangement that works when the
   * describing furniture lives outside the thing it describes.
   */
  let { children } = $props();

  installNavigationGuard();

  /*
   * The sign-in guard belongs here, not only in the root layout.
   *
   * The root layout decides "signed in or not" once, and its effect cannot decide it again while
   * routing *inside* the application: its dependencies — the status, and whether the URL is the login
   * page — are unchanged from one application route to the next, so it never re-runs, and a redirect
   * fired by a page underneath it wins the race. Every route in this group is behind sign-in, so the
   * group asserts that for itself and renders nothing at all until it holds.
   */
  $effect(() => {
    if (session.status === 'signed-out') void goto(resolve('/login'), { replaceState: true });
  });

  // The workspace is loaded once per signed-in session, not once per page — and not again whenever
  // the team list is empty, which it legitimately is for someone just removed from their last team.
  $effect(() => {
    if (session.isSignedIn && !workspace.initialized) {
      void workspace.initialize();
    }
  });

  // SvelteKit only says *that* a newer build exists; the button would rather say which one.
  $effect(() => {
    if (appUpdate.available) void appUpdate.describe();
  });

  const teamId = $derived(workspace.currentTeamId);
  const projectId = $derived(page.params.id ?? null);
  const onProjectPage = $derived(page.url.pathname.startsWith(resolve('/projects/')));

  /* ------------------------------------------------------------ commands ---- */

  function newIssue() {
    if (!teamId) return;

    issueEditor.create({
      teamId,
      // A form opened from inside a project starts in that project.
      projectId: onProjectPage ? projectId : null
    });
  }

  async function newProject() {
    if (teamId) await navigate('/projects/new');
  }

  async function refresh() {
    if (!chrome.refresh) return;
    // Reloading discards edits as thoroughly as leaving does, so it asks the same question.
    if (!(await mayDiscard())) return;

    await chrome.refresh();
  }

  async function signOut() {
    if (!(await mayDiscard())) return;

    session.signOut();
    workspace.reset();
    await realtime.disconnect();
    await goto(resolve('/login'), { replaceState: true });
  }

  const menu = $derived<CommandGroup[]>([
    {
      label: 'File',
      items: [
        {
          label: 'New issue',
          icon: 'plus',
          shortcut: 'c',
          keywords: ['create', 'add', 'task', 'bug'],
          disabled: !teamId,
          run: newIssue
        },
        {
          label: 'New project',
          icon: 'folder-kanban',
          shortcut: 'shift+p',
          keywords: ['create', 'add'],
          disabled: !teamId,
          run: () => void newProject()
        },
        {
          label: 'Refresh',
          icon: 'refresh-cw',
          // Bound only where the page knows what refreshing means; everywhere else F5 is the browser's
          // own reload, which is the right answer there.
          shortcut: chrome.refresh ? 'f5' : undefined,
          keywords: ['reload'],
          disabled: !chrome.refresh,
          run: () => void refresh()
        }
      ]
    },
    {
      label: 'Go to',
      items: [
        {
          label: 'My Issues',
          icon: 'circle-user',
          shortcut: 'g i',
          keywords: ['assigned', 'inbox'],
          run: () => void navigate('/my-issues')
        },
        {
          label: 'Team board',
          icon: 'layout-grid',
          shortcut: 'g b',
          keywords: ['kanban', workspace.currentTeam?.name ?? ''],
          disabled: !teamId,
          run: () => void navigate('/board')
        },
        ...(session.canOpenUserAdmin
          ? [
              {
                label: 'Users & access',
                icon: 'users' as const,
                shortcut: 'g u',
                keywords: ['accounts', 'people', 'administration'],
                run: () => void navigate('/users')
              }
            ]
          : []),
        ...(session.canOpenTeamAdmin
          ? [
              {
                label: 'Teams',
                icon: 'shield' as const,
                shortcut: 'g t',
                keywords: ['members', 'administration'],
                run: () => void navigate('/teams')
              }
            ]
          : []),
        {
          label: 'Preferences',
          icon: 'settings',
          shortcut: 'g s',
          keywords: ['settings', 'profile', 'password', 'theme'],
          run: () => void navigate('/settings')
        }
      ]
    },
    {
      label: 'View',
      items: [
        {
          label: 'Command palette',
          icon: 'command',
          shortcut: 'mod+k',
          run: () => commands.openPalette()
        },
        {
          label: 'Keyboard shortcuts',
          icon: 'command',
          shortcut: '?',
          keywords: ['help', 'keys', 'hotkeys'],
          run: () => commands.openPalette()
        },
        {
          label: settings.sidebarCollapsed ? 'Show sidebar' : 'Hide sidebar',
          icon: 'panel-left',
          shortcut: 'mod+b',
          keywords: ['toggle', 'collapse', 'panel'],
          run: () => settings.toggleSidebar()
        }
      ]
    },
    {
      label: 'Account',
      items: [
        {
          label: 'Sign out',
          icon: 'log-out',
          keywords: ['log out', 'logout'],
          danger: true,
          run: () => void signOut()
        }
      ]
    }
  ]);

  /** What the keyboard and the palette offer: this page's commands first, then the shell's. */
  const pageGroup = $derived<CommandGroup>({ label: chrome.title || 'This page', items: chrome.commands });

  /** The palette also goes anywhere the sidebar does, so a project is a few letters away. */
  const projectGroup = $derived<CommandGroup>({
    label: 'Projects',
    items: workspace.projects
      .filter((project) => !project.archivedAt)
      .map((project) => ({
        label: project.name,
        icon: 'folder' as const,
        keywords: ['project'],
        run: () => void navigate(`/projects/${project.id}`)
      }))
  });

  const bound = $derived([pageGroup, ...menu]);

  /* ----------------------------------------------------------- shortcuts ---- */

  function onKeyDown(event: KeyboardEvent) {
    commands.handle(event, bound);
  }

  /* ------------------------------------------------------------- resize ---- */

  let dragging = $state(false);

  function startResize(event: PointerEvent) {
    dragging = true;
    (event.currentTarget as HTMLElement).setPointerCapture(event.pointerId);
    event.preventDefault();
  }

  function onResize(event: PointerEvent) {
    if (dragging) settings.setSidebarWidth(event.clientX);
  }

  function endResize(event: PointerEvent) {
    dragging = false;
    (event.currentTarget as HTMLElement).releasePointerCapture(event.pointerId);
  }

  /** Keyboard resizing, because a drag handle nobody can reach is a handle half the users do not have. */
  function onHandleKey(event: KeyboardEvent) {
    const step = event.shiftKey ? 32 : 8;

    if (event.key === 'ArrowLeft') {
      event.preventDefault();
      settings.setSidebarWidth(settings.sidebarWidth - step);
    } else if (event.key === 'ArrowRight') {
      event.preventDefault();
      settings.setSidebarWidth(settings.sidebarWidth + step);
    } else if (event.key === 'Enter' || event.key === ' ') {
      event.preventDefault();
      settings.toggleSidebar();
    }
  }
</script>

<svelte:window onkeydown={onKeyDown} />

{#if session.isSignedIn}
<div class="shell" class:collapsed={settings.sidebarCollapsed} style:--sidebar="{settings.sidebarWidth}px">
  <header class="titlebar">
    <AppMenu groups={menu} />

    <a class="brand" href={resolve('/my-issues')}>
      <span class="mark" aria-hidden="true"><Icon name="layout-grid" size={13} /></span>
      <span>Planner</span>
    </a>

    <span class="spacer"></span>

    <button
      type="button"
      class="btn btn-quiet btn-sm palette-trigger"
      onclick={() => commands.openPalette()}
      title="Command palette ({describe('mod+k')})"
      aria-label="Command palette">
      <Icon name="search" size={13} />
      <span class="palette-label">Commands</span>
      <Shortcut shortcut="mod+k" />
    </button>

    <button
      type="button"
      class="btn btn-quiet btn-icon btn-sm"
      onclick={newIssue}
      disabled={!teamId}
      title="New issue ({describe('c')})"
      aria-label="New issue">
      <Icon name="plus" size={15} />
    </button>

    <ThemeToggle />
  </header>

  <aside class="sidebar-slot">
    <Sidebar onSignOut={() => void signOut()} />
  </aside>

  <!--
    A focusable separator is the standard splitter: ARIA gives `separator` an orientation and a
    value when it is focusable, which is exactly this. Svelte's checker treats every div as inert
    regardless of role, so the two rules it raises here are wrong about this element specifically.
  -->
  <!-- svelte-ignore a11y_no_noninteractive_tabindex -->
  <!-- svelte-ignore a11y_no_noninteractive_element_interactions -->
  <div
    class="resizer"
    class:dragging
    role="separator"
    aria-orientation="vertical"
    aria-label="Resize sidebar"
    aria-valuenow={settings.sidebarWidth}
    aria-valuemin={SIDEBAR_MIN}
    aria-valuemax={SIDEBAR_MAX}
    tabindex="0"
    onpointerdown={startResize}
    onpointermove={onResize}
    onpointerup={endResize}
    onkeydown={onHandleKey}>
  </div>

  <main class="content">
    <div class="toolbar">
      {#if settings.sidebarCollapsed}
        <button
          type="button"
          class="btn btn-quiet btn-icon btn-sm"
          onclick={() => settings.toggleSidebar()}
          title="Show sidebar ({describe('mod+b')})"
          aria-label="Show sidebar">
          <Icon name="panel-left" size={15} />
        </button>
      {/if}

      <div class="heading">
        <h1 class="truncate">{chrome.title}</h1>
        {#if chrome.subtitle}<p class="truncate muted">{chrome.subtitle}</p>{/if}
      </div>

      <span class="spacer"></span>

      {#if chrome.actions}
        {@render chrome.actions()}
      {/if}

      {#if chrome.refresh}
        <button
          type="button"
          class="btn btn-quiet btn-icon btn-sm"
          onclick={() => void refresh()}
          title="Refresh (F5)"
          aria-label="Refresh">
          <Icon name="refresh-cw" size={14} class={chrome.busy ? 'spin' : ''} />
        </button>
      {/if}
    </div>

    <div class="page">
      {@render children()}
    </div>
  </main>

  <footer class="statusbar">
    <span class="truncate">{chrome.status ?? ''}</span>
    <span class="spacer"></span>

    <span class="segment" title={realtime.isConnected ? 'Live updates are on' : 'Not connected — this view is as fresh as its last fetch'}>
      <span class="live" class:on={realtime.isConnected}></span>
      {realtime.isConnected ? 'Live' : realtime.status === 'reconnecting' ? 'Reconnecting…' : 'Offline'}
    </span>

    {#if appUpdate.available}
      <span class="segment">
        <button
          type="button"
          class="update"
          title={appUpdate.version ? `Planner ${appUpdate.version} is available — reload to use it` : 'A newer Planner is available — reload to use it'}
          onclick={() => appUpdate.apply()}>
          <Icon name="circle-arrow-up" size={12} />
          Update{appUpdate.version ? ` to ${appUpdate.version}` : ''}
        </button>
      </span>
    {/if}

    <span class="segment version" title={BUILD_LABEL}>{VERSION}</span>
  </footer>
</div>

<IssueEditorDialog />
<CommandPalette groups={[pageGroup, ...menu, projectGroup]} />
<DragGhost />
{/if}

<style>
  .shell {
    display: grid;
    grid-template-rows: var(--titlebar-h) 1fr var(--statusbar-h);
    grid-template-columns: var(--sidebar) 0 1fr;
    grid-template-areas:
      'title title title'
      'side resize content'
      'status status status';
    height: 100%;
    background: var(--bg-app);
  }

  .shell.collapsed {
    grid-template-columns: 0 0 1fr;
  }

  .shell.collapsed .sidebar-slot,
  .shell.collapsed .resizer {
    display: none;
  }

  .titlebar {
    display: flex;
    grid-area: title;
    align-items: center;
    gap: var(--s-2);
    padding: 0 var(--s-3);
    border-bottom: 1px solid var(--border);
    background: var(--bg-app);
  }

  .brand {
    display: flex;
    align-items: center;
    gap: var(--s-3);
    margin-left: var(--s-2);
    color: var(--fg);
    font-size: var(--text-base);
    font-weight: 600;
    letter-spacing: -0.01em;
  }

  .brand:hover {
    text-decoration: none;
  }

  .mark {
    display: grid;
    place-items: center;
    width: 20px;
    height: 20px;
    border-radius: var(--radius-xs);
    background: var(--accent);
    color: var(--fg-on-accent);
  }

  .palette-trigger {
    gap: var(--s-3);
    color: var(--fg-tertiary);
  }

  .sidebar-slot {
    grid-area: side;
    min-width: 0;
    overflow: hidden;
  }

  .resizer {
    grid-area: resize;
    width: 1px;
    margin-right: -3px;
    padding-right: 6px;
    cursor: col-resize;
    touch-action: none;
  }

  .resizer:hover,
  .resizer.dragging,
  .resizer:focus-visible {
    background: var(--accent);
    outline: none;
  }

  .content {
    display: flex;
    grid-area: content;
    flex-direction: column;
    min-width: 0;
    background: var(--bg-surface);
  }

  .toolbar {
    display: flex;
    flex: none;
    align-items: center;
    gap: var(--s-3);
    height: var(--toolbar-h);
    padding: 0 var(--s-5);
    border-bottom: 1px solid var(--border);
    background: var(--bg-app);
  }

  .heading {
    display: flex;
    align-items: baseline;
    gap: var(--s-4);
    min-width: 0;
  }

  h1 {
    font-size: var(--text-md);
    font-weight: 600;
    letter-spacing: -0.01em;
  }

  .heading p {
    font-size: var(--text-sm);
  }

  .page {
    flex: 1;
    min-height: 0;
    overflow: hidden;
  }

  .statusbar {
    display: flex;
    grid-area: status;
    align-items: center;
    gap: var(--s-4);
    padding: 0 var(--s-5);
    border-top: 1px solid var(--border);
    background: var(--bg-app);
    color: var(--fg-tertiary);
    font-size: var(--text-xs);
  }

  .segment {
    display: flex;
    align-items: center;
    gap: var(--s-2);
    padding-left: var(--s-4);
    border-left: 1px solid var(--split);
  }

  .version {
    font-variant-numeric: tabular-nums;
    /* There is a commit behind this number; the cursor is what says so. */
    cursor: help;
  }

  .update {
    display: flex;
    align-items: center;
    gap: var(--s-1);
    padding: 0 var(--s-2);
    border: 0;
    border-radius: var(--radius-sm);
    background: none;
    color: var(--accent);
    font: inherit;
    cursor: pointer;
  }

  .update:hover {
    background: var(--bg-hover);
  }

  .live {
    width: 7px;
    height: 7px;
    border-radius: 50%;
    background: var(--fg-disabled);
  }

  .live.on {
    background: var(--success);
  }

  /* Under about 900px the sidebar is a drawer over the content rather than a column beside it. */
  @media (width <= 900px) {
    .shell:not(.collapsed) {
      grid-template-columns: 0 0 1fr;
    }

    .shell:not(.collapsed) .sidebar-slot {
      position: fixed;
      top: var(--titlebar-h);
      bottom: var(--statusbar-h);
      left: 0;
      z-index: var(--z-sticky);
      width: min(var(--sidebar), 84vw);
      box-shadow: var(--shadow-lg);
    }

    .resizer {
      display: none;
    }

    .palette-label {
      display: none;
    }
  }
</style>
