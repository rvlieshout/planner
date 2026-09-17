<script lang="ts">
  import { ApiError, teams as teamsApi, users as usersApi } from '$lib/api';
  import type {
    Guid,
    OrgRole,
    TeamDto,
    TeamRole,
    UserDetail,
    UserSummary
  } from '$lib/api/types';
  import { ORG_ROLES, TEAM_ROLES } from '$lib/api/types';
  import { Permission, session } from '$lib/auth/session.svelte';
  import { chrome } from '$lib/chrome.svelte';
  import { ORG_ROLE, TEAM_ROLE } from '$lib/meta';
  import { mayDiscard } from '$lib/navigation.svelte';
  import { formatExact, readableOn, relativeTime } from '$lib/format';
  import { workspace } from '$lib/workspace.svelte';
  import Avatar from '$components/Avatar.svelte';
  import Icon from '$components/Icon.svelte';
  import Modal from '$components/Modal.svelte';
  import Select from '$components/Select.svelte';
  import type { SelectOption } from '$components/select';
  import { toasts } from '$components/toast.svelte';

  /**
   * The directory, and who is in which team.
   *
   * Search covers every account including inactive ones, because deactivating someone is not the same
   * as deleting them and finding them again has to be possible. The team table previews *effective*
   * access using the same permission matrix the server enforces: admins and owners administer every
   * team, guests are capped at commenting whatever their team role says, and a member's Viewer role
   * is read-only.
   *
   * Saving is the account followed by the changed memberships, which are separate API operations. If
   * a team change fails, the page reports partial success and keeps the remaining edits for
   * correction and retry rather than throwing them away.
   */
  let users = $state<UserSummary[]>([]);
  let allTeams = $state<TeamDto[]>([]);
  let selected = $state<UserDetail | null>(null);
  let search = $state('');
  let includeInactive = $state(true);
  let loading = $state(true);
  let saving = $state(false);
  let error = $state<string | null>(null);
  let fieldErrors = $state<Record<string, string>>({});

  let displayName = $state('');
  let timeZone = $state('UTC');
  let role = $state<OrgRole>('member');
  let isActive = $state(true);

  let saved = $state({ displayName: '', timeZone: 'UTC', role: 'member' as OrgRole, isActive: true });

  /** Team id to the role this user should have, or null for "not a member". */
  let memberships = $state<Record<Guid, TeamRole | null>>({});
  let savedMemberships = $state<Record<Guid, TeamRole | null>>({});

  let creating = $state(false);
  let newEmail = $state('');
  let newPassword = $state('');
  let newPasswordConfirm = $state('');

  let resetting = $state(false);
  let resetPassword = $state('');
  let resetConfirm = $state('');

  const filtered = $derived(
    users
      .filter((user) => includeInactive || user.isActive)
      .sort(
        (a, b) =>
          Number(b.isActive) - Number(a.isActive) || a.displayName.localeCompare(b.displayName)
      )
  );

  /* ----------------------------------------------------------------- load ---- */

  async function load() {
    loading = true;
    error = null;

    try {
      const [directory, teams] = await Promise.all([
        usersApi.list({ search: search.trim() || undefined, includeInactive, pageSize: 200 }),
        teamsApi.list(true)
      ]);

      users = directory.items;
      allTeams = teams;

      if (selected && !users.some((user) => user.id === selected!.id)) selected = null;
    } catch (failure) {
      error = failure instanceof ApiError ? failure.message : 'Could not load the directory.';
    } finally {
      loading = false;
    }
  }

  $effect(() => {
    void load();
  });

  $effect(() => {
    chrome.set({
      title: 'Users & access',
      subtitle: selected?.displayName ?? (creating ? 'New user' : 'Directory'),
      status: loading ? 'Loading…' : `${users.length} account${users.length === 1 ? '' : 's'}`,
      actions: toolbar,
      commands: [
        { label: 'New user', icon: 'user-plus', disabled: saving, run: () => void startCreate() },
        {
          label: creating ? 'Create user' : 'Save user & team access',
          icon: 'check',
          shortcut: 'mod+s',
          disabled: !(selected || creating) || saving,
          run: () => void save()
        }
      ]
    });

    chrome.refresh = load;
    chrome.busy = loading || saving;
    chrome.unsavedWork = () => (dirty ? 'this account’s unsaved changes' : null);

    return () => chrome.clear();
  });

  async function select(user: UserSummary) {
    if (selected?.id === user.id) return;
    if (!(await mayDiscard())) return;

    creating = false;
    error = null;
    fieldErrors = {};

    try {
      const detail = await usersApi.get(user.id);
      selected = detail;

      displayName = detail.displayName;
      timeZone = detail.timeZone;
      role = detail.role;
      isActive = detail.isActive;
      saved = { displayName, timeZone, role, isActive };

      // One request per team: the API exposes membership per team, not per user.
      const found: Record<Guid, TeamRole | null> = {};

      await Promise.all(
        allTeams.map(async (team) => {
          const members = await teamsApi.members(team.id).catch(() => []);
          found[team.id] = members.find((member) => member.userId === detail.id)?.role ?? null;
        })
      );

      memberships = found;
      savedMemberships = { ...found };
    } catch (failure) {
      error = failure instanceof ApiError ? failure.message : 'Could not open that account.';
    }
  }

  async function startCreate() {
    if (!(await mayDiscard())) return;

    creating = true;
    selected = null;
    error = null;
    fieldErrors = {};

    newEmail = '';
    newPassword = '';
    newPasswordConfirm = '';
    displayName = '';
    timeZone = Intl.DateTimeFormat().resolvedOptions().timeZone || 'UTC';
    role = 'member';
    isActive = true;
    memberships = {};
    savedMemberships = {};
  }

  /* ---------------------------------------------------------------- dirty ---- */

  const membershipsDirty = $derived(
    allTeams.some((team) => (memberships[team.id] ?? null) !== (savedMemberships[team.id] ?? null))
  );

  const dirty = $derived(
    creating
      ? newEmail.trim().length > 0 || displayName.trim().length > 0
      : Boolean(selected) &&
          (displayName !== saved.displayName ||
            timeZone !== saved.timeZone ||
            role !== saved.role ||
            isActive !== saved.isActive ||
            membershipsDirty)
  );

  /* -------------------------------------------------------------- effective ---- */

  /**
   * What this account would actually be able to do in a team, folding both roles together — the same
   * ladder the server's TeamAccess computes.
   */
  function effective(teamRole: TeamRole | null, orgRole: OrgRole): string {
    if (orgRole === 'owner' || orgRole === 'admin') return 'Administer';
    if (teamRole === null) return 'None';
    if (orgRole === 'guest') return 'Comment';

    return teamRole === 'Lead' ? 'Administer' : teamRole === 'Member' ? 'Write' : 'Read';
  }

  /* ----------------------------------------------------------------- save ---- */

  async function save() {
    if (saving) return;

    error = null;
    fieldErrors = {};

    if (!displayName.trim()) {
      fieldErrors = { displayName: 'A display name is required.' };
      return;
    }

    if (creating) {
      if (!newEmail.trim()) {
        fieldErrors = { email: 'An email address is required.' };
        return;
      }

      if (newPassword.length < 12) {
        fieldErrors = { password: 'The password must be at least 12 characters.' };
        return;
      }

      if (newPassword !== newPasswordConfirm) {
        fieldErrors = { passwordConfirm: 'The two passwords do not match.' };
        return;
      }
    }

    saving = true;

    try {
      let userId: Guid;

      if (creating) {
        const created = await usersApi.create({
          email: newEmail.trim(),
          password: newPassword,
          displayName: displayName.trim(),
          role,
          timeZone
        });

        userId = created.id;
        users = [...users, created];
        creating = false;
        await select(created);
      } else if (selected) {
        userId = selected.id;

        const changes: Record<string, unknown> = {};
        if (displayName !== saved.displayName) changes.displayName = displayName.trim();
        if (timeZone !== saved.timeZone) changes.timeZone = timeZone;
        if (role !== saved.role) changes.role = role;
        if (isActive !== saved.isActive) changes.isActive = isActive;

        if (Object.keys(changes).length > 0) {
          const updated = await usersApi.update(userId, changes);
          users = users.map((user) => (user.id === updated.id ? updated : user));
          saved = { displayName, timeZone, role, isActive };
        }
      } else {
        return;
      }

      const failures = await saveMemberships(userId);

      if (failures > 0) {
        error = `The account was saved, but ${failures} team change${failures === 1 ? '' : 's'} could not be. The rest are kept here for another try.`;
      } else {
        toasts.success('Account saved.');
        await workspace.refreshTeams();
      }
    } catch (failure) {
      if (failure instanceof ApiError) {
        error = failure.message;
        fieldErrors = failure.fieldErrors;
      } else {
        error = 'Saving failed.';
      }
    } finally {
      saving = false;
    }
  }

  async function saveMemberships(userId: Guid): Promise<number> {
    let failures = 0;

    for (const team of allTeams) {
      const wanted = memberships[team.id] ?? null;
      const current = savedMemberships[team.id] ?? null;

      if (wanted === current) continue;

      try {
        if (wanted === null) {
          await teamsApi.removeMember(team.id, userId);
        } else if (current === null) {
          await teamsApi.addMember(team.id, userId, wanted);
        } else {
          await teamsApi.updateMember(team.id, userId, wanted);
        }

        savedMemberships = { ...savedMemberships, [team.id]: wanted };
      } catch (failure) {
        // The server's refusals belong to it: a last-lead demotion says so in its own words.
        toasts.error(
          `${team.name}: ${failure instanceof ApiError ? failure.message : 'that change was refused'}`
        );
        failures += 1;
      }
    }

    if (userId === session.user?.id) await session.reloadProfile();
    return failures;
  }

  async function doResetPassword() {
    if (!selected) return;

    if (resetPassword.length < 12) {
      toasts.error('The password must be at least 12 characters.');
      return;
    }

    if (resetPassword !== resetConfirm) {
      toasts.error('The two passwords do not match.');
      return;
    }

    try {
      await usersApi.resetPassword(selected.id, resetPassword);
      resetting = false;
      resetPassword = '';
      resetConfirm = '';
      toasts.success('Password reset.');
    } catch (failure) {
      toasts.error(failure instanceof ApiError ? failure.message : 'That reset was refused.');
    }
  }

  /* --------------------------------------------------------------- options ---- */

  /** Only owners may grant or revoke ownership; nobody may deactivate themselves or an owner. */
  const orgRoleOptions = $derived<SelectOption<OrgRole>[]>(
    ORG_ROLES.map((value) => ({
      value,
      label: ORG_ROLE[value].label,
      icon: ORG_ROLE[value].icon,
      color: ORG_ROLE[value].color,
      hint: ORG_ROLE[value].hint,
      disabled: value === 'owner' && !session.isOwner
    }))
  );

  const teamRoleOptions: SelectOption<TeamRole | ''>[] = [
    { value: '', label: 'Not a member', icon: 'minus', color: 'var(--fg-tertiary)' },
    ...TEAM_ROLES.map((value) => ({
      value,
      label: TEAM_ROLE[value].label,
      icon: TEAM_ROLE[value].icon,
      color: TEAM_ROLE[value].color,
      hint: TEAM_ROLE[value].hint
    }))
  ];

  /**
   * Records the role chosen for one team.
   *
   * The picker hands back the option's value; only the three roles the server knows are stored, and
   * anything else — the empty option — means 'not a member'. Written through a typed copy, because a
   * computed key in an object literal widens the union to plain strings.
   */
  function setMembership(teamId: Guid, chosen: string) {
    const next: Record<Guid, TeamRole | null> = { ...memberships };
    next[teamId] = TEAM_ROLES.find((role) => role === chosen) ?? null;
    memberships = next;
  }

  const canDeactivate = $derived(
    Boolean(selected) && selected!.id !== session.user?.id && selected!.role !== 'owner'
  );
</script>

{#snippet toolbar()}
  {#if dirty}<span class="dirty">Unsaved changes</span>{/if}

  <button type="button" class="btn btn-sm" onclick={() => void startCreate()} disabled={saving}>
    <Icon name="user-plus" size={13} />
    New user
  </button>

  {#if selected || creating}
    <button type="button" class="btn btn-sm btn-primary" onclick={() => void save()} disabled={saving}>
      {creating ? 'Create user' : 'Save user & team access'}
    </button>
  {/if}
{/snippet}

<div class="users">
  <aside class="list">
    <div class="search">
      <Icon name="search" size={13} />
      <input
        bind:value={search}
        class="search-input"
        placeholder="Search the directory…"
        aria-label="Search users"
        onkeydown={(event) => event.key === 'Enter' && load()} />
    </div>

    <label class="toggle">
      <input type="checkbox" bind:checked={includeInactive} onchange={() => void load()} />
      <span>Include inactive</span>
    </label>

    <div class="rows">
      {#each filtered as user (user.id)}
        <button
          type="button"
          class="user"
          class:active={selected?.id === user.id}
          class:inactive={!user.isActive}
          onclick={() => void select(user)}>
          <Avatar name={user.displayName} seed={user.email} size={20} />
          <span class="who">
            <span class="truncate">{user.displayName}</span>
            <span class="truncate email">{user.email}</span>
          </span>
          {#if !user.isActive}<span class="chip">Off</span>{/if}
        </button>
      {:else}
        <p class="muted pad">{loading ? 'Loading…' : 'No accounts match.'}</p>
      {/each}
    </div>
  </aside>

  <div class="detail">
    {#if !selected && !creating}
      <div class="empty">
        <Icon name="users" size={28} />
        <p class="empty-title">Choose an account.</p>
        <p>Its profile, role and team access open here.</p>
      </div>
    {:else}
      {#if error}
        <div class="alert alert-error"><Icon name="circle-alert" size={15} /><span>{error}</span></div>
      {/if}

      <section class="panel">
        <div class="panel-title">
          <span>{creating ? 'New account' : 'Account'}</span>

          {#if selected}
            <div class="row-tight">
              <span class="muted small" title={formatExact(selected.createdAt)}>
                Created {relativeTime(selected.createdAt)}
              </span>
              <button type="button" class="btn btn-sm" onclick={() => (resetting = true)}>
                <Icon name="key-round" size={13} />
                Reset password
              </button>
            </div>
          {/if}
        </div>

        <div class="grid-2">
          <div class="field">
            <label for="user-email">Email</label>
            {#if creating}
              <input
                id="user-email"
                bind:value={newEmail}
                class="input"
                class:invalid={Boolean(fieldErrors.email)}
                type="email"
                disabled={saving} />
              {#if fieldErrors.email}<p class="field-error">{fieldErrors.email}</p>{/if}
            {:else}
              <input id="user-email" class="input" value={selected?.email ?? ''} readonly />
              <p class="muted hint">An email address identifies the account and cannot be changed.</p>
            {/if}
          </div>

          <div class="field">
            <label for="user-name">Display name</label>
            <input
              id="user-name"
              bind:value={displayName}
              class="input"
              class:invalid={Boolean(fieldErrors.displayName)}
              disabled={saving} />
            {#if fieldErrors.displayName}<p class="field-error">{fieldErrors.displayName}</p>{/if}
          </div>

          {#if creating}
            <div class="field">
              <label for="user-password">Password</label>
              <input
                id="user-password"
                bind:value={newPassword}
                class="input"
                class:invalid={Boolean(fieldErrors.password)}
                type="password"
                autocomplete="new-password"
                disabled={saving} />
              <p class="muted hint">At least 12 characters. Length beats character classes.</p>
              {#if fieldErrors.password}<p class="field-error">{fieldErrors.password}</p>{/if}
            </div>

            <div class="field">
              <label for="user-password-confirm">Confirm password</label>
              <input
                id="user-password-confirm"
                bind:value={newPasswordConfirm}
                class="input"
                class:invalid={Boolean(fieldErrors.passwordConfirm)}
                type="password"
                autocomplete="new-password"
                disabled={saving} />
              {#if fieldErrors.passwordConfirm}<p class="field-error">{fieldErrors.passwordConfirm}</p>{/if}
            </div>
          {/if}

          <div class="field">
            <span class="field-label">Organisation role</span>
            <Select
              options={orgRoleOptions}
              value={role}
              onchange={(value) => (role = value)}
              variant="field"
              disabled={saving}
              label="Organisation role" />
            <p class="muted hint">Role changes take effect at the next token refresh or sign-in.</p>
          </div>

          <div class="field">
            <label for="user-tz">Time zone</label>
            <input id="user-tz" bind:value={timeZone} class="input" disabled={saving} />
          </div>
        </div>

        {#if !creating}
          <label class="checkbox">
            <input type="checkbox" bind:checked={isActive} disabled={saving || !canDeactivate} />
            <span>
              Active
              {#if !canDeactivate}
                <span class="muted">— an owner, and your own account, cannot be deactivated here</span>
              {/if}
            </span>
          </label>
        {/if}
      </section>

      <section class="panel">
        <div class="panel-title">
          <span>Team access</span>
          {#if creating}<span class="muted small">Available once the account exists.</span>{/if}
        </div>

        {#if !creating}
          <table class="table">
            <thead>
              <tr>
                <th>Team</th>
                <th>Team role</th>
                <th>Effective access</th>
              </tr>
            </thead>
            <tbody>
              {#each allTeams as team (team.id)}
                <tr>
                  <td>
                    <span class="team">
                      <span class="key" style:background={team.color} style:color={readableOn(team.color)}>
                        {team.key}
                      </span>
                      <span class="truncate">{team.name}</span>
                      {#if team.archivedAt}<span class="chip">Archived</span>{/if}
                    </span>
                  </td>
                  <td>
                    <Select
                      options={teamRoleOptions}
                      value={memberships[team.id] ?? ''}
                      onchange={(value) => setMembership(team.id, value)}
                      disabled={saving || !session.can(team.id, Permission.Administer)}
                      label="{team.name} role" />
                  </td>
                  <td class="muted">{effective(memberships[team.id] ?? null, role)}</td>
                </tr>
              {/each}
            </tbody>
          </table>
        {/if}
      </section>
    {/if}
  </div>
</div>

<Modal
  open={resetting}
  title="Reset password"
  subtitle={selected?.email}
  size="s"
  onclose={() => (resetting = false)}>
  <div class="col">
    <div class="field">
      <label for="reset-password">New password</label>
      <input
        id="reset-password"
        bind:value={resetPassword}
        class="input"
        type="password"
        autocomplete="new-password" />
      <p class="muted hint">At least 12 characters. The current password is not needed.</p>
    </div>

    <div class="field">
      <label for="reset-confirm">Confirm</label>
      <input
        id="reset-confirm"
        bind:value={resetConfirm}
        class="input"
        type="password"
        autocomplete="new-password" />
    </div>
  </div>

  {#snippet footer()}
    <span class="spacer"></span>
    <button type="button" class="btn" onclick={() => (resetting = false)}>Cancel</button>
    <button
      type="button"
      class="btn btn-primary"
      onclick={() => void doResetPassword()}
      disabled={!resetPassword || resetPassword !== resetConfirm}>
      Reset password
    </button>
  {/snippet}
</Modal>

<style>
  .users {
    display: grid;
    grid-template-columns: 280px minmax(0, 1fr);
    height: 100%;
  }

  .list {
    display: flex;
    flex-direction: column;
    min-height: 0;
    border-right: 1px solid var(--border);
    background: var(--bg-app);
  }

  .search {
    display: flex;
    align-items: center;
    gap: var(--s-2);
    padding: 0 var(--s-4);
    border-bottom: 1px solid var(--border);
    color: var(--fg-tertiary);
  }

  .search-input {
    width: 100%;
    height: var(--toolbar-h);
    border: 0;
    background: none;
    font-size: var(--text-base);
    outline: none;
  }

  .toggle,
  .checkbox {
    display: flex;
    align-items: center;
    gap: var(--s-3);
    font-size: var(--text-sm);
  }

  .toggle {
    padding: var(--s-3) var(--s-4);
    border-bottom: 1px solid var(--split);
    color: var(--fg-secondary);
  }

  .checkbox {
    height: var(--control-h);
    font-size: var(--text-base);
  }

  .rows {
    flex: 1;
    min-height: 0;
    padding: var(--s-2);
    overflow-y: auto;
  }

  .user {
    display: flex;
    align-items: center;
    gap: var(--s-3);
    width: 100%;
    height: 38px;
    padding: 0 var(--s-3);
    border: 0;
    border-radius: var(--radius-sm);
    background: none;
    color: var(--fg);
    text-align: left;
    cursor: pointer;
  }

  .user:hover {
    background: var(--bg-hover);
  }

  .user.active {
    background: var(--bg-selected);
  }

  .user.inactive {
    color: var(--fg-tertiary);
  }

  .who {
    display: flex;
    flex: 1;
    flex-direction: column;
    min-width: 0;
    line-height: 1.25;
  }

  .email {
    color: var(--fg-tertiary);
    font-size: var(--text-xs);
  }

  .detail {
    display: flex;
    flex-direction: column;
    gap: var(--s-5);
    max-width: 960px;
    padding: var(--s-6);
    overflow-y: auto;
  }

  .team {
    display: flex;
    align-items: center;
    gap: var(--s-3);
    min-width: 0;
  }

  .key {
    display: grid;
    flex: none;
    place-items: center;
    min-width: 26px;
    height: 18px;
    padding: 0 var(--s-2);
    border-radius: var(--radius-xs);
    font-family: var(--font-mono);
    font-size: 9px;
  }

  .hint,
  .small {
    font-size: var(--text-xs);
  }

  .pad {
    padding: var(--s-4);
    font-size: var(--text-sm);
  }

  .dirty {
    margin-right: var(--s-2);
    color: var(--fg-tertiary);
    font-size: var(--text-sm);
  }

  @media (width <= 900px) {
    .users {
      grid-template-columns: minmax(0, 1fr);
      overflow-y: auto;
    }

    .list {
      border-right: 0;
      border-bottom: 1px solid var(--border);
    }
  }
</style>
