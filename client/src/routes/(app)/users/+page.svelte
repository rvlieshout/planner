<script lang="ts">
  import { resolve } from '$app/paths';
  import { ApiError, invitations, teams as teamsApi, users as usersApi } from '$lib/api';
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
  import { realtime } from '$lib/realtime/hub.svelte';
  import { formatExact, readableOn, relativeTime } from '$lib/format';
  import { workspace } from '$lib/workspace.svelte';
  import Avatar from '$components/Avatar.svelte';
  import Icon from '$components/Icon.svelte';
  import TimeZoneSelect from '$components/TimeZoneSelect.svelte';
  import { defaultTimeZone } from '$lib/regional';
  import Modal from '$components/Modal.svelte';
  import Select from '$components/Select.svelte';
  import type { SelectOption } from '$components/select';
  import { toasts } from '$components/toast.svelte';
  import { confirm } from '$components/confirm.svelte';

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

  let email = $state('');
  let displayName = $state('');
  let timeZone = $state('UTC');
  let role = $state<OrgRole>('member');
  let isActive = $state(true);

  let saved = $state({ email: '', displayName: '', timeZone: 'UTC', role: 'member' as OrgRole, isActive: true });

  /** Team id to the role this user should have, or null for "not a member". */
  let memberships = $state<Record<Guid, TeamRole | null>>({});
  let savedMemberships = $state<Record<Guid, TeamRole | null>>({});

  let creating = $state(false);
  let newEmail = $state('');
  // Invitation credentials stay in memory only, never in persisted browser state.
  let invitation = $state<{ userId: Guid; url: string; expiresAt: string } | null>(null);

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

  // An invitation being accepted, or another administrator's change, shows up without a refresh. The
  // form follows only in the fields nobody is editing, so a half-typed change is not pulled away.
  $effect(() =>
    realtime.on('UserChanged', (change) => {
      if (change.kind === 'Deleted') {
        users = users.filter((existing) => existing.id !== change.id);
        if (selected?.id === change.id) {
          selected = null;
          invitation = null;
        }
        return;
      }

      const user = change.entity;
      if (!user) return;

      users = users.map((existing) => (existing.id === user.id ? user : existing));
      if (selected?.id !== user.id) return;

      selected = { ...selected, ...user };
      if (!user.isInvitationPending) invitation = null;

      if (email === saved.email) email = user.email;
      if (displayName === saved.displayName) displayName = user.displayName;
      if (isActive === saved.isActive) isActive = user.isActive;
      saved = { ...saved, email: user.email, displayName: user.displayName, isActive: user.isActive };
    })
  );

  $effect(() => {
    chrome.set({
      title: 'Users & access',
      subtitle: selected?.displayName ?? (creating ? 'Invite user' : 'Directory'),
      status: loading ? 'Loading…' : `${users.length} account${users.length === 1 ? '' : 's'}`,
      actions: toolbar,
      commands: [
        { label: 'Invite user', icon: 'user-plus', disabled: saving, run: () => void startCreate() },
        {
          label: creating ? 'Create invitation' : 'Save user & team access',
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
    if (saving) return;
    if (selected?.id === user.id) return;
    if (!(await mayDiscard())) return;

    creating = false;
    invitation = null;
    error = null;
    fieldErrors = {};

    try {
      const detail = await usersApi.get(user.id);
      selected = detail;

      email = detail.email;
      displayName = detail.displayName;
      timeZone = detail.timeZone;
      role = detail.role;
      isActive = detail.isActive;
      saved = { email, displayName, timeZone, role, isActive };

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
    if (saving) return;
    if (!(await mayDiscard())) return;

    creating = true;
    selected = null;
    invitation = null;
    error = null;
    fieldErrors = {};

    newEmail = '';
    displayName = '';
    timeZone = defaultTimeZone();
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
          (email !== saved.email ||
            displayName !== saved.displayName ||
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

    if (creating ? !newEmail.trim() : !email.trim()) {
      fieldErrors = { email: 'An email address is required.' };
      return;
    }

    saving = true;

    try {
      let userId: Guid;

      if (creating) {
        const issued = await invitations.create({
          email: newEmail.trim(),
          displayName: displayName.trim(),
          role,
          timeZone
        });

        const created = issued.user;
        userId = created.id;
        users = [...users, created];
        creating = false;
        // Do not reload/select here: a failed detail fetch must not hide a successfully issued link.
        selected = created;
        email = created.email;
        displayName = created.displayName;
        timeZone = created.timeZone;
        role = created.role;
        isActive = created.isActive;
        saved = { email, displayName, timeZone, role, isActive };
        savedMemberships = {};
        showInvitation(created.id, issued.token, issued.expiresAt);
      } else if (selected) {
        userId = selected.id;

        const changes: Record<string, unknown> = {};
        if (email !== saved.email) changes.email = email.trim();
        if (displayName !== saved.displayName) changes.displayName = displayName.trim();
        if (timeZone !== saved.timeZone) changes.timeZone = timeZone;
        if (role !== saved.role) changes.role = role;
        if (isActive !== saved.isActive) changes.isActive = isActive;

        if (Object.keys(changes).length > 0) {
          const updated = await usersApi.update(userId, changes);
          users = users.map((user) => (user.id === updated.id ? updated : user));
          selected = { ...selected, ...updated };
          email = updated.email;
          saved = { email, displayName, timeZone, role, isActive };
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

  function showInvitation(userId: Guid, token: string, expiresAt: string) {
    const url = new URL(resolve('/accept-invitation'), window.location.origin);
    url.hash = new URLSearchParams({ userId, token }).toString();
    invitation = { userId, url: url.href, expiresAt };
  }

  /** Whether a pending account's latest link still works. The server clears the expiry on revocation. */
  function invitationState(user: UserSummary | null): 'live' | 'expired' | 'revoked' | null {
    if (!user?.isInvitationPending) return null;
    if (!user.invitationExpiresAt) return 'revoked';

    return new Date(user.invitationExpiresAt).getTime() > Date.now() ? 'live' : 'expired';
  }

  const INVITATION = {
    live: { chip: 'Invited', title: 'Invitation pending' },
    expired: { chip: 'Invite expired', title: 'Invitation expired' },
    revoked: { chip: 'Invite revoked', title: 'Invitation revoked' }
  } as const;

  const pendingState = $derived(invitationState(selected));

  function setInvitationExpiry(userId: Guid, expiresAt: string | null) {
    users = users.map((user) => (user.id === userId ? { ...user, invitationExpiresAt: expiresAt } : user));
    if (selected?.id === userId) selected = { ...selected, invitationExpiresAt: expiresAt };
  }

  async function renewInvitation() {
    if (!selected || saving) return;
    saving = true;
    error = null;
    try {
      const issued = await invitations.renew(selected.id);
      showInvitation(issued.user.id, issued.token, issued.expiresAt);
      setInvitationExpiry(issued.user.id, issued.expiresAt);
      toasts.success('New invitation created. Previous links no longer work.');
    } catch (failure) {
      error = failure instanceof ApiError ? failure.message : 'Could not reissue the invitation.';
    } finally {
      saving = false;
    }
  }

  async function copyInvitation() {
    if (!invitation) return;
    try {
      await navigator.clipboard.writeText(invitation.url);
      toasts.success('Invitation link copied.');
    } catch {
      toasts.error('Could not copy automatically. Select the invitation link and copy it manually.');
    }
  }

  async function revokeInvitation() {
    if (!selected?.isInvitationPending || saving) return;
    const userId = selected.id;
    if (!await confirm.ask({
      title: 'Revoke invitation?',
      message: 'All invitation links for this account will stop working. The account stays inactive. You can reissue an invitation later.',
      confirmLabel: 'Revoke invitation', cancelLabel: 'Cancel', danger: true
    })) return;
    if (saving || selected?.id !== userId) return;
    saving = true;
    error = null;
    try {
      await usersApi.deactivate(userId);
      invitation = null;
      setInvitationExpiry(userId, null);
      toasts.success('Invitation revoked. Previous links no longer work.');
    } catch (failure) {
      error = failure instanceof ApiError ? failure.message : 'Could not revoke the invitation.';
    } finally {
      saving = false;
    }
  }

  /** An account nobody has signed in to has authored nothing, so it can go entirely. */
  const canDelete = $derived(
    Boolean(selected) && !selected!.lastSeenAt && (selected!.role !== 'owner' || session.isOwner)
  );

  async function deleteAccount() {
    if (!selected || saving) return;
    const userId = selected.id;
    if (!await confirm.ask({
      title: 'Delete account?',
      message: `${selected.displayName} (${selected.email}) has never signed in. The account, its invitation links and its team memberships are removed, and anything assigned to it becomes unassigned. This cannot be undone.`,
      confirmLabel: 'Delete account', cancelLabel: 'Cancel', danger: true
    })) return;
    if (saving || selected?.id !== userId) return;
    saving = true;
    error = null;
    try {
      await usersApi.delete(userId);
      users = users.filter((user) => user.id !== userId);
      selected = null;
      invitation = null;
      toasts.success('Account deleted.');
      await workspace.refreshTeams();
    } catch (failure) {
      error = failure instanceof ApiError ? failure.message : 'Could not delete the account.';
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
    Invite user
  </button>

  {#if selected || creating}
    <button type="button" class="btn btn-sm btn-primary" onclick={() => void save()} disabled={saving}>
      {creating ? 'Create invitation' : 'Save user & team access'}
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
          disabled={saving}
          onclick={() => void select(user)}>
          <Avatar name={user.displayName} seed={user.email} size={20} />
          <span class="who">
            <span class="truncate">{user.displayName}</span>
            <span class="truncate email">{user.email}</span>
          </span>
          {#if user.isInvitationPending}<span class="chip">{INVITATION[invitationState(user)!].chip}</span>{/if}
          {#if !user.isActive && !user.isInvitationPending}<span class="chip">Off</span>{/if}
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
          <span>{creating ? 'Invite a new user' : 'Account'}</span>

          {#if selected}
            <div class="row-tight">
              <span class="muted small" title={formatExact(selected.createdAt)}>
                Created {relativeTime(selected.createdAt)}
              </span>
              {#if !selected.isInvitationPending}
                <button
                  type="button"
                  class="btn btn-sm"
                  onclick={() => (resetting = true)}
                  disabled={saving}>
                  <Icon name="key-round" size={13} />
                  Reset password
                </button>
              {/if}
              {#if canDelete}
                <button
                  type="button"
                  class="btn btn-sm"
                  title="This account has never signed in"
                  onclick={() => void deleteAccount()}
                  disabled={saving}>
                  <Icon name="trash-2" size={13} />
                  Delete account
                </button>
              {/if}
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
            {:else if selected?.isInvitationPending}
              <input
                id="user-email"
                bind:value={email}
                class="input"
                class:invalid={Boolean(fieldErrors.email)}
                type="email"
                disabled={saving} />
              {#if fieldErrors.email}<p class="field-error">{fieldErrors.email}</p>{/if}
              <p class="muted hint">
                Can be corrected until the invitation is accepted. Links already shared keep working.
              </p>
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
            <TimeZoneSelect id="user-tz" bind:value={timeZone} disabled={saving} />
          </div>
        </div>

        {#if !creating && !selected?.isInvitationPending}
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

      {#if creating}
        <p class="muted">
          Create an expiring, single-use invitation link to share manually. No email is sent.
          The recipient chooses their own password when accepting.
        </p>
      {:else if selected && pendingState}
        <section class="panel">
          <div class="panel-title">
            <span>{INVITATION[pendingState].title}</span>
            <button
              type="button"
              class="btn btn-sm"
              disabled={saving || (selected.role === 'owner' && !session.isOwner)}
              onclick={() => void renewInvitation()}>Reissue invitation</button>
          </div>
          <p class="muted">
            No email is sent. Share the link privately with the intended recipient.
            Reissuing invalidates all previous invitation links.
          </p>
          {#if invitation?.userId === selected.id}
            <div class="field">
              <label for="invitation-link">Invitation link</label>
              <input
                id="invitation-link"
                class="input"
                value={invitation.url}
                readonly
                onclick={(event) => event.currentTarget.select()} />
              <p class="muted hint">
                Expires {formatExact(invitation.expiresAt)}. This link is only shown now;
                copy it before leaving this account.
              </p>
            </div>
            <button type="button" class="btn" onclick={() => void copyInvitation()}>Copy invitation link</button>
          {:else if pendingState === 'live'}
            <p class="muted hint">
              The current link expires {formatExact(selected.invitationExpiresAt!)}. It was only shown
              when it was created; reissue the invitation to get a new link to copy.
            </p>
          {:else if pendingState === 'expired'}
            <p class="muted hint">
              The last link expired {formatExact(selected.invitationExpiresAt!)}. Reissue the invitation
              to generate a new link to copy.
            </p>
          {:else}
            <p class="muted hint">No link is active. Reissue the invitation to generate a new link to copy.</p>
          {/if}
          <p class="muted hint">This account becomes active only after the invitation is accepted.</p>
          <button type="button" class="btn btn-sm"
            disabled={saving || pendingState === 'revoked' || (selected.role === 'owner' && !session.isOwner)}
            onclick={() => void revokeInvitation()}>Revoke invitation links</button>
        </section>
      {/if}

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
