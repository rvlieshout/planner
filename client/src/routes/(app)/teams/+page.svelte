<script lang="ts">
  import { ApiError, teams as teamsApi, users as usersApi } from '$lib/api';
  import type { Guid, TeamDto, TeamMemberDto, TeamRole, UserSummary } from '$lib/api/types';
  import { TEAM_ROLES } from '$lib/api/types';
  import { Permission, session } from '$lib/auth/session.svelte';
  import { chrome } from '$lib/chrome.svelte';
  import { TEAM_ROLE } from '$lib/meta';
  import { mayDiscard } from '$lib/navigation.svelte';
  import { readableOn, relativeTime } from '$lib/format';
  import { workspace } from '$lib/workspace.svelte';
  import Avatar from '$components/Avatar.svelte';
  import ColorPicker from '$components/ColorPicker.svelte';
  import Icon from '$components/Icon.svelte';
  import Select from '$components/Select.svelte';
  import type { SelectOption } from '$components/select';
  import { confirm } from '$components/confirm.svelte';
  import { toasts } from '$components/toast.svelte';

  /**
   * Teams: their settings, and who is in them.
   *
   * The list is the teams you may *change*, which is a different list from the switcher in the
   * sidebar — that one is every team you may read. Archived teams are listed last rather than left
   * out, because restoring one is only possible from a list that still shows it.
   *
   * Creating flows straight into filling in, exactly as the project page does: a successful create
   * turns this into that team's page, the creator is already its first lead, and the membership
   * section below comes to life.
   */
  let teams = $state<TeamDto[]>([]);
  let selectedId = $state<Guid | null>(null);
  let creating = $state(false);
  let loading = $state(true);
  let saving = $state(false);
  let error = $state<string | null>(null);
  let fieldErrors = $state<Record<string, string>>({});

  let key = $state('');
  let name = $state('');
  let description = $state('');
  let color = $state('#5E6AD2');
  let isPrivate = $state(false);

  let saved = $state({ name: '', description: '', color: '#5E6AD2', isPrivate: false });

  interface MemberRow {
    member: TeamMemberDto;
    role: TeamRole;
    busy: boolean;
    error: string | null;
  }

  let memberRows = $state<MemberRow[]>([]);
  let directory = $state<UserSummary[]>([]);
  let addUserId = $state<Guid | ''>('');
  let addRole = $state<TeamRole>('Member');

  const selected = $derived(teams.find((team) => team.id === selectedId) ?? null);
  const canAdminister = $derived(selected ? session.can(selected.id, Permission.Administer) : false);

  const sorted = $derived(
    [...teams].sort(
      (a, b) =>
        Number(Boolean(a.archivedAt)) - Number(Boolean(b.archivedAt)) || a.name.localeCompare(b.name)
    )
  );

  /* ----------------------------------------------------------------- load ---- */

  async function load() {
    loading = true;
    error = null;

    try {
      const all = await teamsApi.list(true);

      // Only the ones this caller may change. An owner or administrator administers every team; a
      // team lead administers the teams they lead; a guest administers nothing whatever their team
      // role says.
      teams = all.filter((team) => session.can(team.id, Permission.Administer));

      if (selectedId && !teams.some((team) => team.id === selectedId)) selectedId = null;
      if (selectedId) await loadTeam(selectedId);
    } catch (failure) {
      error = failure instanceof ApiError ? failure.message : 'Could not load the teams.';
    } finally {
      loading = false;
    }
  }

  async function loadTeam(teamId: Guid) {
    const team = teams.find((candidate) => candidate.id === teamId);
    if (!team) return;

    key = team.key;
    name = team.name;
    description = team.description ?? '';
    color = team.color;
    isPrivate = team.isPrivate;
    saved = { name, description, color, isPrivate };

    memberRows = (await teamsApi.members(teamId)).map((member) => ({
      member,
      role: member.role,
      busy: false,
      error: null
    }));

    // The picker offers active accounts who are not already members.
    directory = (await usersApi.list({ pageSize: 200 })).items;
  }

  $effect(() => {
    void load();
  });

  $effect(() => {
    chrome.set({
      title: 'Teams',
      subtitle: selected ? selected.name : creating ? 'New team' : 'Settings and membership',
      status: loading ? 'Loading…' : `${teams.length} team${teams.length === 1 ? '' : 's'} you administer`,
      actions: toolbar
    });

    chrome.refresh = load;
    chrome.busy = loading || saving;
    chrome.unsavedWork = () => (dirty ? 'this team’s unsaved changes' : null);

    return () => chrome.clear();
  });

  /* ---------------------------------------------------------------- dirty ---- */

  const rowDirty = (row: MemberRow) => row.role !== row.member.role;

  const dirty = $derived(
    Boolean(selectedId || creating) &&
      (name !== saved.name ||
        description !== saved.description ||
        color !== saved.color ||
        isPrivate !== saved.isPrivate ||
        memberRows.some(rowDirty))
  );

  /* -------------------------------------------------------------- actions ---- */

  async function select(teamId: Guid) {
    if (teamId === selectedId) return;
    if (!(await mayDiscard())) return;

    creating = false;
    selectedId = teamId;
    error = null;
    fieldErrors = {};
    await loadTeam(teamId);
  }

  async function startCreate() {
    if (!(await mayDiscard())) return;

    creating = true;
    selectedId = null;
    error = null;
    fieldErrors = {};

    key = '';
    name = '';
    description = '';
    color = '#5E6AD2';
    isPrivate = false;
    saved = { name: '', description: '', color: '#5E6AD2', isPrivate: false };
    memberRows = [];
  }

  /**
   * The key prefixes every issue identifier the team ever writes, so the server does not let it
   * change. It is checked here as well — upper-cased, 1–8 characters, starting with a letter —
   * because a rejected key is otherwise a round trip to learn a typo.
   */
  function validateKey(): string | null {
    const value = key.trim().toUpperCase();

    if (!value) return 'A key is required.';
    if (value.length > 8) return 'The key must be 8 characters or fewer.';
    if (!/^[A-Z][A-Z0-9]*$/.test(value)) return 'The key must start with a letter, and hold only letters and digits.';

    return null;
  }

  async function save() {
    if (saving) return;

    fieldErrors = {};
    error = null;

    if (!name.trim()) {
      fieldErrors = { name: 'A name is required.' };
      return;
    }

    if (creating) {
      const keyProblem = validateKey();

      if (keyProblem) {
        fieldErrors = { key: keyProblem };
        return;
      }
    }

    saving = true;

    try {
      if (creating) {
        const created = await teamsApi.create({
          key: key.trim().toUpperCase(),
          name: name.trim(),
          description: description.trim() || null,
          color,
          isPrivate
        });

        teams = [...teams, created];
        creating = false;
        selectedId = created.id;

        await workspace.refreshTeams();
        await loadTeam(created.id);
        toasts.success(`${created.key} created. You are its first lead.`);
      } else if (selectedId) {
        const changes: Record<string, unknown> = {};

        if (name !== saved.name) changes.name = name.trim();
        if (description !== saved.description) changes.description = description.trim() || null;
        if (color !== saved.color) changes.color = color;
        if (isPrivate !== saved.isPrivate) changes.isPrivate = isPrivate;

        if (Object.keys(changes).length > 0) {
          const updated = await teamsApi.update(selectedId, changes);
          teams = teams.map((team) => (team.id === updated.id ? updated : team));
          saved = { name, description, color, isPrivate };
          await workspace.refreshTeams();
        }

        const failures = await saveMembers();

        if (failures > 0) {
          error = `The team was saved, but ${failures} membership change${failures === 1 ? '' : 's'} could not be.`;
        } else {
          toasts.success('Team saved.');
        }
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

  async function saveMembers(): Promise<number> {
    if (!selectedId) return 0;

    let failures = 0;

    for (const row of memberRows) {
      if (!rowDirty(row)) continue;

      row.busy = true;
      row.error = null;

      try {
        row.member = await teamsApi.updateMember(selectedId, row.member.userId, row.role);
      } catch (failure) {
        // The server refuses demoting a team's only lead, and says so in its own words.
        row.error = failure instanceof ApiError ? failure.message : 'That change was refused.';
        row.role = row.member.role;
        failures += 1;
      } finally {
        row.busy = false;
      }
    }

    // A lead who demotes or removes themselves loses the team from this page at that moment, because
    // the authority the page was working with has just been handed back.
    await session.reloadProfile();
    return failures;
  }

  async function addMember() {
    if (!selectedId || !addUserId) return;

    saving = true;

    try {
      const member = await teamsApi.addMember(selectedId, addUserId, addRole);
      memberRows = [...memberRows, { member, role: member.role, busy: false, error: null }];
      addUserId = '';
    } catch (failure) {
      error = failure instanceof ApiError ? failure.message : 'Adding that member failed.';
    } finally {
      saving = false;
    }
  }

  async function removeMember(row: MemberRow) {
    if (!selectedId) return;

    const answer = await confirm.ask({
      title: `Remove ${row.member.displayName}?`,
      message: 'Their account and everything they wrote in this team stay exactly as they are.',
      confirmLabel: 'Remove',
      cancelLabel: 'Cancel',
      danger: true
    });

    if (!answer) return;

    try {
      await teamsApi.removeMember(selectedId, row.member.userId);
      memberRows = memberRows.filter((candidate) => candidate !== row);
    } catch (failure) {
      row.error = failure instanceof ApiError ? failure.message : 'That removal was refused.';
    }
  }

  /** Archiving is how a team ends: deleting one would take its projects, issues and history with it. */
  async function toggleArchive() {
    if (!selected) return;

    const archiving = !selected.archivedAt;

    if (archiving) {
      const answer = await confirm.ask({
        title: `Archive ${selected.name}?`,
        message:
          'It leaves the switcher and the sidebar. Its projects, issues and history are untouched, and Restore puts it back.',
        confirmLabel: 'Archive',
        cancelLabel: 'Cancel'
      });

      if (!answer) return;
    }

    try {
      const updated = archiving
        ? await teamsApi.archive(selected.id)
        : await teamsApi.restore(selected.id);

      teams = teams.map((team) => (team.id === updated.id ? updated : team));
      await workspace.refreshTeams();
    } catch (failure) {
      toasts.error(failure instanceof ApiError ? failure.message : 'That did not work.');
    }
  }

  /* --------------------------------------------------------------- options ---- */

  const roleOptions: SelectOption<TeamRole>[] = TEAM_ROLES.map((role) => ({
    value: role,
    label: TEAM_ROLE[role].label,
    icon: TEAM_ROLE[role].icon,
    color: TEAM_ROLE[role].color,
    hint: TEAM_ROLE[role].hint
  }));

  const candidateOptions = $derived<SelectOption<Guid | ''>[]>([
    { value: '', label: 'Choose someone…', icon: 'user-plus', color: 'var(--fg-tertiary)' },
    ...directory
      .filter((user) => user.isActive && !memberRows.some((row) => row.member.userId === user.id))
      .sort((a, b) => a.displayName.localeCompare(b.displayName))
      .map((user) => ({
        value: user.id,
        label: user.displayName,
        avatarName: user.displayName,
        hint: user.email
      }))
  ]);
</script>

{#snippet toolbar()}
  {#if dirty}<span class="dirty">Unsaved changes</span>{/if}

  {#if session.isAdmin}
    <button type="button" class="btn btn-sm" onclick={() => void startCreate()} disabled={saving}>
      <Icon name="plus" size={13} />
      New team
    </button>
  {/if}

  {#if selected || creating}
    <button
      type="button"
      class="btn btn-sm btn-primary"
      onclick={() => void save()}
      disabled={saving || !name.trim()}>
      {creating ? 'Create team' : 'Save changes'}
    </button>
  {/if}
{/snippet}

<div class="teams">
  <aside class="list">
    {#if loading && teams.length === 0}
      <p class="muted pad">Loading…</p>
    {/if}

    {#each sorted as team (team.id)}
      <button
        type="button"
        class="team"
        class:active={team.id === selectedId}
        onclick={() => void select(team.id)}>
        <span class="key" style:background={team.color} style:color={readableOn(team.color)}>
          {team.key}
        </span>
        <span class="truncate">{team.name}</span>
        {#if team.archivedAt}<span class="chip">Archived</span>{/if}
        {#if team.isPrivate}<Icon name="shield" size={12} />{/if}
      </button>
    {/each}

    {#if teams.length === 0 && !loading}
      <p class="muted pad">
        {session.isAdmin ? 'No teams yet. Create the first one.' : 'You do not administer any team.'}
      </p>
    {/if}
  </aside>

  <div class="detail">
    {#if !selected && !creating}
      <div class="empty">
        <Icon name="shield" size={28} />
        <p class="empty-title">Choose a team.</p>
        <p>Its settings and membership open here.</p>
      </div>
    {:else}
      {#if error}
        <div class="alert alert-error"><Icon name="circle-alert" size={15} /><span>{error}</span></div>
      {/if}

      <section class="panel">
        <div class="panel-title">
          <span>{creating ? 'New team' : 'Settings'}</span>

          {#if selected}
            <button type="button" class="btn btn-sm" onclick={() => void toggleArchive()} disabled={!canAdminister}>
              <Icon name={selected.archivedAt ? 'archive-restore' : 'archive'} size={13} />
              {selected.archivedAt ? 'Restore' : 'Archive'}
            </button>
          {/if}
        </div>

        <div class="grid-2">
          <div class="field">
            <label for="team-key">Key</label>
            <input
              id="team-key"
              bind:value={key}
              class="input mono"
              class:invalid={Boolean(fieldErrors.key)}
              maxlength="8"
              placeholder="ENG"
              readonly={!creating}
              disabled={saving}
              oninput={() => (key = key.toUpperCase())} />
            <p class="muted hint">
              {creating
                ? 'Prefixes every issue in this team. It cannot be changed afterwards.'
                : 'Set once, at creation: it prefixes every issue this team has ever written.'}
            </p>
            {#if fieldErrors.key}<p class="field-error">{fieldErrors.key}</p>{/if}
          </div>

          <div class="field">
            <label for="team-name">Name</label>
            <input
              id="team-name"
              bind:value={name}
              class="input"
              class:invalid={Boolean(fieldErrors.name)}
              placeholder="Engineering"
              disabled={saving} />
            {#if fieldErrors.name}<p class="field-error">{fieldErrors.name}</p>{/if}
          </div>
        </div>

        <div class="field">
          <label for="team-description">Description</label>
          <textarea id="team-description" bind:value={description} class="textarea" rows="3" disabled={saving}></textarea>
        </div>

        <div class="grid-2">
          <div class="field">
            <span class="field-label">Colour</span>
            <ColorPicker value={color} onchange={(value) => (color = value)} disabled={saving} />
          </div>

          <div class="field">
            <span class="field-label">Visibility</span>
            <label class="checkbox">
              <input type="checkbox" bind:checked={isPrivate} disabled={saving} />
              <span>Private — only members can see this team</span>
            </label>
          </div>
        </div>
      </section>

      <section class="panel">
        <div class="panel-title">
          <span>Membership</span>
          <span class="badge">{memberRows.length}</span>
        </div>

        {#if creating}
          <p class="muted">You become this team's first lead when it is created.</p>
        {:else}
          <table class="table">
            <thead>
              <tr>
                <th>Member</th>
                <th>Role</th>
                <th>Since</th>
                <th></th>
              </tr>
            </thead>
            <tbody>
              {#each memberRows as row (row.member.userId)}
                <tr>
                  <td>
                    <span class="who">
                      <Avatar name={row.member.displayName} seed={row.member.email} size={20} />
                      <span class="truncate">{row.member.displayName}</span>
                      <span class="muted truncate">{row.member.email}</span>
                    </span>
                  </td>
                  <td>
                    <div class="role">
                      <Select
                        options={roleOptions}
                        value={row.role}
                        onchange={(value) => (row.role = value)}
                        disabled={row.busy || saving || !canAdminister}
                        label="Role" />
                      {#if rowDirty(row)}<span class="mark" title="Unsaved"><Icon name="pencil" size={11} /></span>{/if}
                    </div>
                    {#if row.error}<p class="field-error">{row.error}</p>{/if}
                  </td>
                  <td class="muted">{relativeTime(row.member.createdAt)}</td>
                  <td>
                    <button
                      type="button"
                      class="btn btn-quiet btn-icon btn-sm"
                      onclick={() => void removeMember(row)}
                      disabled={row.busy || saving || !canAdminister}
                      aria-label="Remove {row.member.displayName}">
                      <Icon name="x" size={13} />
                    </button>
                  </td>
                </tr>
              {/each}
            </tbody>
          </table>

          {#if canAdminister}
            <div class="add-member">
              <Select
                options={candidateOptions}
                value={addUserId}
                onchange={(value) => (addUserId = value)}
                variant="field"
                disabled={saving}
                label="Add a member" />

              <Select
                options={roleOptions}
                value={addRole}
                onchange={(value) => (addRole = value)}
                variant="field"
                disabled={saving}
                label="Role for the new member" />

              <button
                type="button"
                class="btn btn-sm"
                onclick={() => void addMember()}
                disabled={saving || !addUserId}>
                <Icon name="user-plus" size={13} />
                Add
              </button>
            </div>
          {/if}
        {/if}
      </section>
    {/if}
  </div>
</div>

<style>
  .teams {
    display: grid;
    grid-template-columns: 260px minmax(0, 1fr);
    height: 100%;
  }

  .list {
    display: flex;
    flex-direction: column;
    gap: var(--s-1);
    padding: var(--s-4) var(--s-3);
    border-right: 1px solid var(--border);
    background: var(--bg-app);
    overflow-y: auto;
  }

  .team {
    display: flex;
    align-items: center;
    gap: var(--s-3);
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

  .team:hover {
    background: var(--bg-hover);
  }

  .team.active {
    background: var(--bg-selected);
    color: var(--accent);
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

  .detail {
    display: flex;
    flex-direction: column;
    gap: var(--s-5);
    max-width: 900px;
    padding: var(--s-6);
    overflow-y: auto;
  }

  .who {
    display: flex;
    align-items: center;
    gap: var(--s-3);
    min-width: 0;
  }

  .role {
    display: flex;
    align-items: center;
    gap: var(--s-2);
  }

  .mark {
    color: var(--accent);
  }

  .checkbox {
    display: flex;
    align-items: center;
    gap: var(--s-3);
    height: var(--control-h);
    font-size: var(--text-base);
  }

  .add-member {
    display: grid;
    grid-template-columns: minmax(0, 1fr) 160px auto;
    gap: var(--s-3);
    padding-top: var(--s-4);
    border-top: 1px solid var(--split);
  }

  .hint {
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

  @media (width <= 860px) {
    .teams {
      grid-template-columns: minmax(0, 1fr);
      overflow-y: auto;
    }

    .list {
      flex-direction: row;
      flex-wrap: wrap;
      border-right: 0;
      border-bottom: 1px solid var(--border);
    }

    .add-member {
      grid-template-columns: minmax(0, 1fr);
    }
  }
</style>
