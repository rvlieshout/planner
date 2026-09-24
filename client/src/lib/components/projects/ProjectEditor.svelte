<script lang="ts">
  import DateInput from '$components/DateInput.svelte';
  import { ApiError, milestones as milestonesApi, projects as projectsApi } from '$lib/api';
  import type {
    CreateProjectRequest,
    Guid,
    MilestoneDto,
    MilestoneStatus,
    ProjectDto,
    ProjectHealth,
    ProjectStatus,
    UpdateProjectRequest
  } from '$lib/api/types';
  import { MILESTONE_STATUSES, PROJECT_HEALTHS, PROJECT_STATUSES } from '$lib/api/types';
  import { chrome } from '$lib/chrome.svelte';
  import { Permission, session } from '$lib/auth/session.svelte';
  import { MILESTONE_STATUS, PROJECT_HEALTH, PROJECT_STATUS } from '$lib/meta';
  import { navigate } from '$lib/navigation.svelte';
  import { realtime } from '$lib/realtime/hub.svelte';
  import { workspace } from '$lib/workspace.svelte';
  import Icon from '$components/Icon.svelte';
  import EditableMarkdown from '$components/markdown/EditableMarkdown.svelte';
  import ColorPicker from '$components/ColorPicker.svelte';
  import Progress from '$components/Progress.svelte';
  import ProjectHistoryPurge from '$components/projects/ProjectHistoryPurge.svelte';
  import Select from '$components/Select.svelte';
  import type { SelectOption } from '$components/select';
  import { confirm } from '$components/confirm.svelte';
  import { toasts } from '$components/toast.svelte';

  /**
   * Creating and maintaining a project.
   *
   * Projects get a page rather than a dialog. An issue is one answer and a modal is the right shape
   * for it; a project is something you create and then keep — it carries milestones, each its own
   * resource on the server, and maintaining those is a session rather than a single answer.
   *
   * A successful create turns this page into that project's settings page rather than closing it: the
   * id arrives, the heading changes, and the milestone section — which needs something to POST to —
   * comes to life underneath.
   */
  interface Props {
    /** Absent for a new project. */
    projectId?: Guid | null;
  }

  let { projectId = null }: Props = $props();

  const NONE = '';

  // Both follow the prop through the effect below, which also runs on first mount.
  let id = $state<Guid | null>(null);
  let loading = $state(true);
  let saving = $state(false);
  let deleting = $state(false);
  let loadedTeamId = $state<Guid | null>(null);
  let error = $state<string | null>(null);
  let fieldErrors = $state<Record<string, string>>({});

  let name = $state('');
  let summary = $state('');
  let description = $state('');
  let status = $state<ProjectStatus>('Backlog');
  let health = $state<ProjectHealth>('OnTrack');
  let color = $state('#5E6AD2');
  let leadUserId = $state<Guid | ''>(NONE);
  let startDate = $state('');
  let targetDate = $state('');

  let saved = $state({
    name: '',
    summary: '',
    description: '',
    status: 'Backlog' as ProjectStatus,
    health: 'OnTrack' as ProjectHealth,
    color: '#5E6AD2',
    leadUserId: NONE as Guid | '',
    startDate: '',
    targetDate: ''
  });

  interface MilestoneRow {
    milestone: MilestoneDto;
    name: string;
    targetDate: string;
    status: MilestoneStatus;
    busy: boolean;
    error: string | null;
  }

  let milestoneRows = $state<MilestoneRow[]>([]);
  let newMilestoneName = $state('');
  let newMilestoneDate = $state('');

  const teamId = $derived(workspace.currentTeamId);
  const members = $derived(workspace.membersNow(teamId));

  // Members are needed for the lead picker and are not part of the project request.
  $effect(() => {
    if (teamId) void workspace.membersFor(teamId);
  });

  async function load() {
    if (!id) {
      loading = false;
      return;
    }

    loading = true;
    error = null;

    try {
      apply(await projectsApi.get(id));
      milestoneRows = (await projectsApi.milestones(id)).map(toRow);
    } catch (failure) {
      error = failure instanceof ApiError ? failure.message : 'Could not load that project.';
    } finally {
      loading = false;
    }
  }

  function apply(project: ProjectDto) {
    loadedTeamId = project.teamId;
    name = project.name;
    summary = project.summary ?? '';
    description = project.description ?? '';
    status = project.status;
    health = project.health;
    color = project.color;
    leadUserId = project.lead?.id ?? NONE;
    startDate = project.startDate ?? '';
    targetDate = project.targetDate ?? '';

    saved = { name, summary, description, status, health, color, leadUserId, startDate, targetDate };
  }

  const toRow = (milestone: MilestoneDto): MilestoneRow => ({
    milestone,
    name: milestone.name,
    targetDate: milestone.targetDate ?? '',
    status: milestone.status,
    busy: false,
    error: null
  });

  $effect(() => {
    void projectId;
    id = projectId;
    void load();
  });

  /*
   * The issue rollup beside each milestone, kept live.
   *
   * It is counted from the milestone's issues when it is read, so it moves whenever anyone creates,
   * completes, reassigns, archives or deletes one — and the API republishes the milestone for
   * exactly those writes. This page holds no issues of its own to count them from, so it takes the
   * server's numbers.
   */
  $effect(() =>
    realtime.on('MilestoneChanged', (change) => {
      const entity = change.entity;
      if (!entity || entity.projectId !== id) return;

      const row = milestoneRows.find((candidate) => candidate.milestone.id === entity.id);
      if (!row) return;

      // The rollup only. `row.milestone` is the baseline `rowDirty` measures the form against, so
      // replacing it wholesale would reinterpret edits that have not been saved yet: a name being
      // typed here would quietly read as clean against someone else's rename, and Save changes
      // would then skip the row.
      row.milestone = { ...row.milestone, progress: entity.progress };
    })
  );

  /* ---------------------------------------------------------------- dirty ---- */

  const rowDirty = (row: MilestoneRow) =>
    row.name !== row.milestone.name ||
    row.targetDate !== (row.milestone.targetDate ?? '') ||
    row.status !== row.milestone.status;

  /**
   * Computed from the fields rather than tracked beside them, so the mark in the footer and the
   * prompt the navigator raises can never disagree — and a milestone typed into the add row and
   * never added counts, because losing it is losing work just the same.
   */
  const dirty = $derived(
    name !== saved.name ||
      summary !== saved.summary ||
      description !== saved.description ||
      status !== saved.status ||
      health !== saved.health ||
      color !== saved.color ||
      leadUserId !== saved.leadUserId ||
      startDate !== saved.startDate ||
      targetDate !== saved.targetDate ||
      milestoneRows.some(rowDirty) ||
      newMilestoneName.trim().length > 0
  );

  $effect(() => {
    chrome.set({
      title: id ? name || 'Project' : 'New project',
      subtitle: workspace.currentTeam?.name,
      status: dirty ? 'Unsaved changes' : id ? 'Saved' : 'Not created yet',
      actions: toolbar
    });

    chrome.refresh = load;
    chrome.busy = loading || saving || deleting;
    chrome.unsavedWork = () => (dirty ? 'this project’s unsaved changes' : null);

    return () => chrome.clear();
  });

  /* ----------------------------------------------------------------- save ---- */

  function projectDiff(): UpdateProjectRequest {
    const changes: UpdateProjectRequest = {};

    if (name !== saved.name) changes.name = name.trim();
    if (summary !== saved.summary) changes.summary = summary.trim() || null;
    if (description !== saved.description) changes.description = description.trim() || null;
    if (status !== saved.status) changes.status = status;
    if (health !== saved.health) changes.health = health;
    if (color !== saved.color) changes.color = color;
    if (leadUserId !== saved.leadUserId) changes.leadUserId = leadUserId || null;
    if (startDate !== saved.startDate) changes.startDate = startDate || null;
    if (targetDate !== saved.targetDate) changes.targetDate = targetDate || null;

    return changes;
  }

  /**
   * Save changes saves the *page*: the project's own fields, every dirty milestone row, and anything
   * typed into the add row and not yet added. It has to — the footer shows one unsaved mark covering
   * all of that, and a button called "Save changes" that leaves the mark standing is a button that
   * lies.
   */
  async function save() {
    if (saving || deleting) return;

    if (!name.trim()) {
      fieldErrors = { name: 'A name is required.' };
      return;
    }

    if (!teamId) {
      error = 'Choose a team first.';
      return;
    }

    saving = true;
    error = null;
    fieldErrors = {};

    try {
      if (id) {
        const changes = projectDiff();
        if (Object.keys(changes).length > 0) apply(await projectsApi.update(id, changes));
      } else {
        const body: CreateProjectRequest = {
          teamId,
          name: name.trim(),
          summary: summary.trim() || null,
          description: description.trim() || null,
          status,
          health,
          color,
          leadUserId: leadUserId || null,
          startDate: startDate || null,
          targetDate: targetDate || null
        };

        const created = await projectsApi.create(body);
        id = created.id;
        apply(created);

        // The page becomes that project's settings page: same page, now with an id to POST
        // milestones to. Replacing the URL is what makes the browser's back button behave.
        await navigate(`/projects/${created.id}/settings`, { force: true });
      }

      await workspace.loadProjects();
    } catch (failure) {
      if (failure instanceof ApiError) {
        error = failure.message;
        fieldErrors = failure.fieldErrors;
      } else {
        error = 'Saving failed.';
      }

      saving = false;
      return;
    }

    // Rows are saved one at a time after the project. Any the server refuses keep their own error and
    // stay dirty, and the page reports how many rather than claiming success over work still sitting
    // there.
    const failures = await saveMilestones();

    saving = false;

    if (failures === 0) {
      toasts.success('Project saved.');
    } else {
      error = `The project was saved, but ${failures} milestone${failures === 1 ? '' : 's'} could not be.`;
    }
  }

  async function saveMilestones(): Promise<number> {
    if (!id) return 0;

    let failures = 0;

    for (const row of milestoneRows) {
      if (!rowDirty(row)) continue;

      row.busy = true;
      row.error = null;

      try {
        row.milestone = await milestonesApi.update(row.milestone.id, {
          name: row.name.trim(),
          targetDate: row.targetDate || null,
          status: row.status
        });
      } catch (failure) {
        row.error = failure instanceof ApiError ? failure.message : 'Saving this milestone failed.';
        failures += 1;
      } finally {
        row.busy = false;
      }
    }

    if (newMilestoneName.trim()) {
      try {
        const created = await projectsApi.createMilestone(id, {
          name: newMilestoneName.trim(),
          targetDate: newMilestoneDate || null
        });

        milestoneRows = [...milestoneRows, toRow(created)];
        newMilestoneName = '';
        newMilestoneDate = '';
      } catch (failure) {
        toasts.error(failure instanceof ApiError ? failure.message : 'Adding that milestone failed.');
        failures += 1;
      }
    }

    return failures;
  }

  async function addMilestone() {
    if (!id || !newMilestoneName.trim()) return;

    saving = true;
    await saveMilestones();
    saving = false;
  }

  async function removeMilestone(row: MilestoneRow) {
    const answer = await confirm.ask({
      title: `Delete ${row.milestone.name}?`,
      message: 'The milestone goes. Its issues stay in the project, without one.',
      confirmLabel: 'Delete',
      cancelLabel: 'Cancel',
      danger: true
    });

    if (!answer) return;

    try {
      await milestonesApi.remove(row.milestone.id);
      milestoneRows = milestoneRows.filter((candidate) => candidate !== row);
    } catch (failure) {
      row.error = failure instanceof ApiError ? failure.message : 'Deleting that milestone failed.';
    }
  }

  async function close() {
    // Back to the project's board when there is one; the board is where the work is.
    await navigate(id ? `/projects/${id}` : '/board');
  }

  async function deleteProject() {
    if (!id || loading || saving || deleting || !session.can(loadedTeamId, Permission.Administer)) return;

    const projectToDelete = id;
    const projectName = saved.name;
    deleting = true;

    try {
      const answer = await confirm.ask({
        title: 'Are you sure!',
        message: `Permanently delete “${projectName}” and its milestones? Issues and documents stay in the team. Unsaved changes will be discarded. This cannot be undone.`,
        requiredText: projectName,
        confirmLabel: 'Delete project',
        cancelLabel: 'Cancel',
        danger: true
      });

      if (!answer || id !== projectToDelete) return;

      await projectsApi.remove(projectToDelete);
      workspace.projects = workspace.projects.filter((project) => project.id !== projectToDelete);
      toasts.success('Project deleted.');
      await navigate('/board', { force: true });
    } catch (failure) {
      toasts.error(failure instanceof ApiError ? failure.message : 'Deleting that project failed.');
    } finally {
      deleting = false;
    }
  }

  /* --------------------------------------------------------------- options ---- */

  const statusOptions = $derived<SelectOption<ProjectStatus>[]>(
    PROJECT_STATUSES.map((value) => ({
      value,
      label: PROJECT_STATUS[value].label,
      icon: PROJECT_STATUS[value].icon,
      color: PROJECT_STATUS[value].color
    }))
  );

  const healthOptions = $derived<SelectOption<ProjectHealth>[]>(
    PROJECT_HEALTHS.map((value) => ({
      value,
      label: PROJECT_HEALTH[value].label,
      icon: PROJECT_HEALTH[value].icon,
      color: PROJECT_HEALTH[value].color
    }))
  );

  const leadOptions = $derived<SelectOption<Guid | ''>[]>([
    { value: NONE, label: 'No lead', icon: 'user', color: 'var(--fg-tertiary)' },
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

  const milestoneStatusOptions = $derived<SelectOption<MilestoneStatus>[]>(
    MILESTONE_STATUSES.map((value) => ({
      value,
      label: MILESTONE_STATUS[value].label,
      icon: MILESTONE_STATUS[value].icon,
      color: MILESTONE_STATUS[value].color
    }))
  );
</script>

{#snippet toolbar()}
  {#if dirty}<span class="dirty">Unsaved changes</span>{/if}

  <button type="button" class="btn btn-sm" onclick={() => void close()} disabled={deleting}>Close</button>
  <button
    type="button"
    class="btn btn-sm btn-primary"
    onclick={() => void save()}
    disabled={saving || deleting || !name.trim()}>
    {#if saving}<Icon name="loader-circle" size={13} class="spin" />{/if}
    {id ? 'Save changes' : 'Create project'}
  </button>
{/snippet}

<div class="editor">
  {#if loading}
    <div class="empty"><Icon name="loader-circle" size={20} class="spin" /></div>
  {:else}
    {#if error}
      <div class="alert alert-error"><Icon name="circle-alert" size={15} /><span>{error}</span></div>
    {/if}

    <section class="panel">
      <div class="panel-title">
        <span>Project</span>
      </div>

      <div class="field">
        <label for="project-name">Name</label>
        <input
          id="project-name"
          bind:value={name}
          class="input"
          class:invalid={Boolean(fieldErrors.name)}
          placeholder="Apollo"
          disabled={saving} />
        {#if fieldErrors.name}<p class="field-error">{fieldErrors.name}</p>{/if}
      </div>

      <div class="field">
        <label for="project-summary">Summary</label>
        <input
          id="project-summary"
          bind:value={summary}
          class="input"
          placeholder="One line about what this project is for"
          disabled={saving} />
      </div>

      <div class="field">
        <span class="field-label">Description</span>
        <EditableMarkdown
          bind:value={description}
          canEdit={!saving}
          rows={6}
          placeholder="What this project is, in as much detail as it deserves…"
          label="Project description" />
      </div>

      <div class="grid-2">
        <div class="field">
          <span class="field-label">Status</span>
          <Select options={statusOptions} value={status} onchange={(v) => (status = v)} variant="field" disabled={saving} label="Status" />
        </div>

        <div class="field">
          <span class="field-label">Health</span>
          <Select options={healthOptions} value={health} onchange={(v) => (health = v)} variant="field" disabled={saving} label="Health" />
        </div>

        <div class="field">
          <span class="field-label">Lead</span>
          <Select options={leadOptions} value={leadUserId} onchange={(v) => (leadUserId = v)} variant="field" disabled={saving} label="Lead" />
        </div>

        <div class="field">
          <span class="field-label">Colour</span>
          <ColorPicker value={color} onchange={(v) => (color = v)} disabled={saving} />
        </div>

        <div class="field">
          <label for="project-start">Start date</label>
          <DateInput id="project-start" bind:value={startDate} class="input" disabled={saving} />
        </div>

        <div class="field">
          <label for="project-target">Target date</label>
          <DateInput id="project-target" bind:value={targetDate} class="input" disabled={saving} />
        </div>
      </div>
    </section>

    <section class="panel">
      <div class="panel-title">
        <span>Milestones</span>
        {#if !id}<span class="muted small">Available once the project exists.</span>{/if}
      </div>

      {#if id}
        <ul class="milestones">
          {#each milestoneRows as row (row.milestone.id)}
            <li class="milestone" class:dirty-row={rowDirty(row)}>
              <input
                bind:value={row.name}
                class="input name"
                aria-label="Milestone name"
                disabled={row.busy || saving} />

              <DateInput
                bind:value={row.targetDate}
                class="input date"
                aria-label="Target date"
                disabled={row.busy || saving} />

              <div class="status">
                <Select
                  options={milestoneStatusOptions}
                  value={row.status}
                  onchange={(v) => (row.status = v)}
                  variant="field"
                  disabled={row.busy || saving}
                  label="Milestone status" />
              </div>

              <div class="rollup">
                <Progress progress={row.milestone.progress} showCounts={false} />
                <span class="badge">{row.milestone.progress.completed}/{row.milestone.progress.total}</span>
              </div>

              <!-- The tick appears only once the row differs from the server, which makes it the
                   row's unsaved mark as well as its save button. -->
              {#if rowDirty(row)}
                <span class="mark" title="Unsaved"><Icon name="pencil" size={12} /></span>
              {/if}

              <button
                type="button"
                class="btn btn-quiet btn-icon btn-sm"
                onclick={() => void removeMilestone(row)}
                disabled={row.busy || saving}
                aria-label="Delete {row.milestone.name}">
                <Icon name="trash-2" size={13} />
              </button>

              {#if row.error}<p class="field-error row-error">{row.error}</p>{/if}
            </li>
          {:else}
            <li class="muted small">No milestones yet.</li>
          {/each}
        </ul>

        <div class="add-row">
          <input
            bind:value={newMilestoneName}
            class="input name"
            placeholder="New milestone"
            aria-label="New milestone name"
            disabled={saving} />
          <DateInput
            bind:value={newMilestoneDate}
            class="input date"
            aria-label="New milestone target date"
            disabled={saving} />
          <button
            type="button"
            class="btn btn-sm"
            onclick={() => void addMilestone()}
            disabled={saving || !newMilestoneName.trim()}>
            <Icon name="plus" size={13} />
            Add
          </button>
        </div>
      {/if}
    </section>
    {#if id && saved.name && session.isAdmin}
      <ProjectHistoryPurge projectId={id} projectName={saved.name} />
    {/if}
    {#if id && saved.name && session.can(loadedTeamId, Permission.Administer)}
      <section class="panel">
        <div class="panel-title"><span>Delete project</span></div>
        <p class="muted small">Permanently delete this project and its milestones. Issues and documents stay in the team.</p>
        <button
          type="button"
          class="btn btn-danger"
          disabled={saving || deleting}
          onclick={() => void deleteProject()}>
          <Icon name={deleting ? 'loader-circle' : 'trash-2'} size={14} class={deleting ? 'spin' : ''} />
          {deleting ? 'Deleting…' : 'Delete project'}
        </button>
      </section>
    {/if}
  {/if}
</div>

<style>
  .editor {
    display: flex;
    flex-direction: column;
    gap: var(--s-6);
    height: 100%;
    max-width: 920px;
    padding: var(--s-6) var(--s-6) var(--s-10);
    overflow-y: auto;
  }

  .milestones {
    display: flex;
    flex-direction: column;
    gap: var(--s-2);
  }

  .milestone,
  .add-row {
    display: grid;
    grid-template-columns: minmax(0, 1fr) 150px 150px minmax(120px, 200px) auto auto;
    gap: var(--s-3);
    align-items: center;
  }

  .add-row {
    grid-template-columns: minmax(0, 1fr) 150px auto;
    padding-top: var(--s-4);
    border-top: 1px solid var(--split);
  }

  .milestone.dirty-row .name {
    border-color: var(--accent-border);
  }

  .rollup {
    display: flex;
    align-items: center;
    gap: var(--s-3);
  }

  .mark {
    display: inline-flex;
    color: var(--accent);
  }

  .row-error {
    grid-column: 1 / -1;
  }

  .dirty {
    margin-right: var(--s-2);
    color: var(--fg-tertiary);
    font-size: var(--text-sm);
  }

  .small {
    font-size: var(--text-sm);
  }

  @media (width <= 900px) {
    .milestone,
    .add-row {
      grid-template-columns: minmax(0, 1fr) auto;
    }

    .rollup {
      grid-column: 1 / -1;
    }
  }
</style>
