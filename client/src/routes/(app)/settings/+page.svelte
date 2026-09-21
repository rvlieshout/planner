<script lang="ts">
  import { ApiError, me as meApi } from '$lib/api';
  import { session } from '$lib/auth/session.svelte';
  import { chrome } from '$lib/chrome.svelte';
  import { settings, type ThemeChoice } from '$lib/settings.svelte';
  import { regional } from '$lib/regional.svelte';
  import { calendarDate } from '$lib/regional';
  import { realtime } from '$lib/realtime/hub.svelte';
  import { BUILD_SHA, VERSION } from '$lib/version';
  import { ORG_ROLE, TEAM_ROLE } from '$lib/meta';
  import Avatar from '$components/Avatar.svelte';
  import Icon from '$components/Icon.svelte';
  import TimeZoneSelect from '$components/TimeZoneSelect.svelte';
  import { toasts } from '$components/toast.svelte';

  /**
   * Your own account, and how this browser shows it.
   *
   * The two halves are genuinely different things: the profile lives on the server and follows you to
   * any machine, while the appearance and the sidebar are about this browser and are kept here.
   */
  let displayName = $state(session.user?.displayName ?? '');
  let timeZone = $state(session.user?.timeZone ?? 'UTC');
  let savingProfile = $state(false);
  let profileError = $state<string | null>(null);

  let currentPassword = $state('');
  let newPassword = $state('');
  let confirmPassword = $state('');
  let changingPassword = $state(false);
  let passwordError = $state<string | null>(null);

  const profileDirty = $derived(
    displayName !== (session.user?.displayName ?? '') || timeZone !== (session.user?.timeZone ?? 'UTC')
  );

  const THEMES: { value: ThemeChoice; label: string; hint: string }[] = [
    { value: 'system', label: 'Match system', hint: 'Follows the operating system setting.' },
    { value: 'light', label: 'Light', hint: 'Always light, whatever the system does.' },
    { value: 'dark', label: 'Dark', hint: 'Always dark, whatever the system does.' }
  ];

  $effect(() => {
    chrome.set({
      title: 'Preferences',
      subtitle: session.user?.email,
      status: `Planner ${VERSION}`,
      commands: [
        {
          label: 'Save profile',
          icon: 'check',
          shortcut: 'mod+s',
          disabled: savingProfile || !profileDirty,
          run: () => void saveProfile()
        }
      ]
    });

    chrome.unsavedWork = () => (profileDirty ? 'your profile changes' : null);

    return () => chrome.clear();
  });

  async function saveProfile() {
    if (savingProfile || !profileDirty) return;

    savingProfile = true;
    profileError = null;

    try {
      session.applyProfile(
        await meApi.update({ displayName: displayName.trim(), timeZone: timeZone.trim() })
      );

      toasts.success('Profile saved.');
    } catch (failure) {
      profileError = failure instanceof ApiError ? failure.message : 'Saving your profile failed.';
    } finally {
      savingProfile = false;
    }
  }

  async function changePassword() {
    passwordError = null;

    if (newPassword.length < 12) {
      passwordError = 'The new password must be at least 12 characters.';
      return;
    }

    if (newPassword !== confirmPassword) {
      passwordError = 'The two new passwords do not match.';
      return;
    }

    changingPassword = true;

    try {
      await meApi.changePassword({ currentPassword, newPassword });

      currentPassword = '';
      newPassword = '';
      confirmPassword = '';
      toasts.success('Password changed.');
    } catch (failure) {
      passwordError = failure instanceof ApiError ? failure.message : 'Changing your password failed.';
    } finally {
      changingPassword = false;
    }
  }
</script>

<div class="settings">
  <section class="panel">
    <div class="panel-title"><span>Profile</span></div>

    <div class="identity">
      <Avatar
        name={session.user?.displayName}
        src={session.user?.avatarUrl}
        seed={session.user?.email}
        size={44} />
      <div>
        <p class="name">{session.user?.displayName}</p>
        <p class="muted">{session.user?.email}</p>
        {#if session.role}
          <p class="muted small">
            <Icon name={ORG_ROLE[session.role].icon} size={12} />
            {ORG_ROLE[session.role].label} — {ORG_ROLE[session.role].hint}
          </p>
        {/if}
      </div>
    </div>

    {#if profileError}
      <div class="alert alert-error"><Icon name="circle-alert" size={15} /><span>{profileError}</span></div>
    {/if}

    <div class="grid-2">
      <div class="field">
        <label for="me-name">Display name</label>
        <input id="me-name" bind:value={displayName} class="input" disabled={savingProfile} />
      </div>

      <div class="field">
        <label for="me-tz">Time zone</label>
        <TimeZoneSelect id="me-tz" bind:value={timeZone} disabled={savingProfile} />
      </div>
    </div>

    <div class="actions">
      <button
        type="button"
        class="btn btn-primary btn-sm"
        onclick={() => void saveProfile()}
        disabled={savingProfile || !profileDirty}>
        Save profile
      </button>
    </div>
  </section>

  <section class="panel">
    <div class="panel-title"><span>Password</span></div>

    {#if passwordError}
      <div class="alert alert-error"><Icon name="circle-alert" size={15} /><span>{passwordError}</span></div>
    {/if}

    <div class="grid-2">
      <div class="field">
        <label for="pw-current">Current password</label>
        <input
          id="pw-current"
          bind:value={currentPassword}
          class="input"
          type="password"
          autocomplete="current-password" />
      </div>

      <div class="field">
        <label for="pw-new">New password</label>
        <input
          id="pw-new"
          bind:value={newPassword}
          class="input"
          type="password"
          autocomplete="new-password" />
        <p class="muted hint">At least 12 characters.</p>
      </div>

      <div class="field">
        <label for="pw-confirm">Confirm new password</label>
        <input
          id="pw-confirm"
          bind:value={confirmPassword}
          class="input"
          type="password"
          autocomplete="new-password" />
      </div>
    </div>

    <div class="actions">
      <button
        type="button"
        class="btn btn-sm"
        onclick={() => void changePassword()}
        disabled={changingPassword || !currentPassword || !newPassword}>
        Change password
      </button>
    </div>
  </section>

  <section class="panel">
    <div class="panel-title"><span>Date and time</span></div>
    <div class="field">
      <label for="date-locale">Regional format</label>
      <select id="date-locale" class="input" value={settings.dateLocale}
        onchange={(event) => settings.setDateLocale(event.currentTarget.value)}>
        <option value="auto">Automatic (timezone hint, then browser)</option>
        <option value="nl-NL">Nederlands (Nederland)</option>
        <option value="en-GB">English (United Kingdom)</option>
        <option value="en-US">English (United States)</option>
        <option value="de-DE">Deutsch (Deutschland)</option>
        <option value="fr-FR">Français (France)</option>
      </select>
      <p class="muted hint">Europe/Amsterdam selects Dutch formats automatically. This preference is saved in this browser; the interface stays English.</p>
      <p class="muted hint">Example: {calendarDate('2026-09-18', regional.locale)} · {new Intl.DateTimeFormat(regional.locale, { hour: '2-digit', minute: '2-digit', timeZone: 'UTC' }).format(new Date('2026-09-18T14:30:00Z'))}</p>
    </div>
  </section>

  <section class="panel">
    <div class="panel-title"><span>Appearance</span></div>

    <div class="themes">
      {#each THEMES as theme (theme.value)}
        <button
          type="button"
          class="theme"
          class:selected={settings.theme === theme.value}
          onclick={() => settings.setTheme(theme.value)}>
          <span class="theme-label">{theme.label}</span>
          <span class="muted small">{theme.hint}</span>
          {#if settings.theme === theme.value}<Icon name="check" size={14} class="tick" />{/if}
        </button>
      {/each}
    </div>

    <label class="checkbox">
      <input type="checkbox" checked={settings.sidebarCollapsed} onchange={() => settings.toggleSidebar()} />
      <span>Hide the sidebar <kbd>Ctrl+B</kbd></span>
    </label>
  </section>

  <section class="panel">
    <div class="panel-title"><span>This session</span></div>

    <dl class="facts">
      <dt>Version</dt>
      <dd>{VERSION}</dd>

      <dt>Build</dt>
      <dd class="sha">{BUILD_SHA}</dd>

      <dt>Live updates</dt>
      <dd>
        <span class="live" class:on={realtime.isConnected}></span>
        {realtime.isConnected ? `Connected — ${realtime.groups.length} groups` : 'Not connected'}
      </dd>

      <dt>Teams</dt>
      <dd>
        {#each session.teams as membership, index (membership.teamId)}
          {index > 0 ? ', ' : ''}{membership.teamName}
          <span class="muted">({TEAM_ROLE[membership.role].label})</span>
        {:else}
          <span class="muted">None yet.</span>
        {/each}
      </dd>
    </dl>
  </section>
</div>

<style>
  .settings {
    display: flex;
    flex-direction: column;
    gap: var(--s-5);
    max-width: 760px;
    height: 100%;
    padding: var(--s-6) var(--s-6) var(--s-10);
    overflow-y: auto;
  }

  .identity {
    display: flex;
    align-items: center;
    gap: var(--s-5);
  }

  .name {
    font-size: var(--text-md);
    font-weight: 600;
  }

  .identity .small {
    display: flex;
    align-items: center;
    gap: var(--s-2);
    margin-top: var(--s-1);
  }

  .actions {
    display: flex;
    justify-content: flex-end;
  }

  .themes {
    display: grid;
    grid-template-columns: repeat(auto-fit, minmax(180px, 1fr));
    gap: var(--s-3);
  }

  .theme {
    display: flex;
    flex-direction: column;
    gap: var(--s-1);
    padding: var(--s-4);
    border: 1px solid var(--border);
    border-radius: var(--radius-sm);
    background: var(--bg-surface);
    text-align: left;
    cursor: pointer;
  }

  .theme:hover {
    border-color: var(--border-strong);
  }

  .theme.selected {
    border-color: var(--accent);
    background: var(--accent-subtle);
  }

  .theme-label {
    font-weight: 500;
  }

  .theme :global(.tick) {
    position: absolute;
    color: var(--accent);
  }

  .checkbox {
    display: flex;
    align-items: center;
    gap: var(--s-3);
  }

  kbd {
    padding: 1px var(--s-2);
    border: 1px solid var(--border);
    border-radius: var(--radius-xs);
    background: var(--bg-sunken);
    color: var(--fg-tertiary);
    font-family: var(--font-sans);
    font-size: var(--text-xs);
  }

  .facts {
    display: grid;
    grid-template-columns: auto minmax(0, 1fr);
    gap: var(--s-3) var(--s-5);
    font-size: var(--text-base);
  }

  .facts dt {
    color: var(--fg-tertiary);
  }

  .facts dd {
    display: flex;
    flex-wrap: wrap;
    align-items: center;
    gap: var(--s-2);
  }

  /* The whole commit, in full, because the point of it here is to be read off and pasted. */
  .sha {
    font-family: var(--font-mono);
    font-size: var(--text-sm);
    overflow-wrap: anywhere;
    user-select: all;
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

  .hint,
  .small {
    font-size: var(--text-xs);
  }
</style>
