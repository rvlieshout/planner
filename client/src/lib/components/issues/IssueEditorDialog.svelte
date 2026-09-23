<script lang="ts">
  import DateInput from '$components/DateInput.svelte';
  import Modal from '$components/Modal.svelte';
  import Icon from '$components/Icon.svelte';
  import Select from '$components/Select.svelte';
  import type { SelectOption } from '$components/select';
  import { alpha } from '$lib/format';
  import { ApiError, issues as issuesApi, projects as projectsApi } from '$lib/api';
  import type {
    CreateIssueRequest,
    Guid,
    IssuePriority,
    IssueSummary,
    LabelDto,
    MilestoneDto,
    TeamMemberDto,
    UpdateIssueRequest,
    WorkflowStateDto
  } from '$lib/api/types';
  import { ESTIMATE_SCALE, PRIORITY, PRIORITY_ORDER } from '$lib/meta';
  import { issueEditor } from '$lib/issues/editor.svelte';
  import { announce } from '$lib/issues/changes';
  import { workspace, loadIssueFormData } from '$lib/workspace.svelte';
  import { toasts } from '$components/toast.svelte';
  import { confirm } from '$components/confirm.svelte';
  import { commands } from '$lib/commands.svelte';

  /**
   * One form, both directions.
   *
   * New issue opens it empty; opening an existing one fetches the detail, because the summary a card
   * carries has no description. Only the title is ever required — the server fills in the team's
   * default state, no priority and no assignee — so an issue can be captured in two keystrokes and
   * fleshed out later. That is the difference between a tracker people use and one they route around.
   *
   * Saving an existing issue sends a **diff**. The values the form held when it opened are remembered
   * and only the fields actually changed are included, so two people editing different fields of the
   * same issue do not overwrite each other, and an untouched description cannot be blanked by a form
   * that merely happened to be holding it.
   */
  const NONE = '';

  interface Draft {
    title: string;
    description: string;
    stateId: Guid;
    priority: IssuePriority;
    assigneeId: Guid | '';
    projectId: Guid | '';
    milestoneId: Guid | '';
    estimate: number | 0;
    dueDate: string;
    labelIds: Guid[];
  }

  const target = $derived(issueEditor.target);
  const editing = $derived(Boolean(target?.issueId));

  let draft = $state<Draft | null>(null);
  let original = $state<Draft | null>(null);
  let issueKey = $state<string | null>(null);
  let teamId = $state<Guid | null>(null);

  let states = $state<WorkflowStateDto[]>([]);
  let members = $state<TeamMemberDto[]>([]);
  let labels = $state<LabelDto[]>([]);
  let milestones = $state<MilestoneDto[]>([]);
  let estimateScale = $state<number[]>(ESTIMATE_SCALE);

  let loading = $state(false);
  let saving = $state(false);
  let error = $state<string | null>(null);
  let fieldErrors = $state<Record<string, string>>({});

  let titleInput = $state<HTMLTextAreaElement | null>(null);
  let dialog = $state<HTMLDialogElement | null>(null);

  // Opening is an effect rather than a call, because the dialog is mounted once and the thing that
  // opens it may be three components away.
  $effect(() => {
    const request = issueEditor.target;

    if (!request) {
      draft = null;
      original = null;
      return;
    }

    void load(request.issueId, request);
  });

  async function load(issueId: Guid | undefined, request: NonNullable<typeof target>) {
    loading = true;
    error = null;
    fieldErrors = {};
    milestones = [];
    issueKey = null;

    try {
      if (issueId) {
        const issue = await issuesApi.get(issueId);
        teamId = issue.teamId;
        issueKey = issue.key;

        await loadTeamData(issue.teamId);

        // Anything the server holds that is not on the scale is added to it, so opening an issue
        // estimated by some other means and saving it cannot quietly round the number away.
        estimateScale =
          issue.estimate && !ESTIMATE_SCALE.includes(issue.estimate)
            ? [...ESTIMATE_SCALE, issue.estimate].sort((a, b) => a - b)
            : ESTIMATE_SCALE;

        const next: Draft = {
          title: issue.title,
          description: issue.description ?? '',
          stateId: issue.stateId,
          priority: issue.priority,
          assigneeId: issue.assignee?.id ?? NONE,
          projectId: issue.projectId ?? NONE,
          milestoneId: issue.milestoneId ?? NONE,
          estimate: issue.estimate ?? 0,
          dueDate: issue.dueDate ?? '',
          labelIds: issue.labels.map((label) => label.id)
        };

        draft = next;
        original = { ...next, labelIds: [...next.labelIds] };

        if (issue.projectId) await loadMilestones(issue.projectId);
      } else {
        const team = request.teamId ?? workspace.currentTeamId;
        if (!team) throw new ApiError(400, 'Choose a team before creating an issue.');

        teamId = team;
        await loadTeamData(team);
        estimateScale = ESTIMATE_SCALE;

        const next: Draft = {
          title: '',
          description: '',
          stateId:
            states.find((state) => state.id === request.stateId)?.id ??
            states.find((state) => state.isDefault)?.id ??
            states[0]?.id ??
            NONE,
          priority: 'None',
          assigneeId: NONE,
          projectId: request.projectId ?? NONE,
          milestoneId: request.milestoneId ?? NONE,
          estimate: 0,
          dueDate: '',
          labelIds: []
        };

        draft = next;
        original = null;

        if (next.projectId) await loadMilestones(next.projectId);
      }

      queueMicrotask(() => titleInput?.focus());
    } catch (failure) {
      error = failure instanceof Error ? failure.message : 'Could not open that issue.';
    } finally {
      loading = false;
    }
  }

  async function loadTeamData(team: Guid) {
    const data = await loadIssueFormData(team);
    states = data.states;
    members = data.members;
    labels = data.labels;
  }

  async function loadMilestones(projectId: Guid) {
    milestones = await projectsApi.milestones(projectId).catch(() => []);
  }

  /** Changing project changes which milestones exist, so the held one is cleared rather than kept. */
  async function onProjectChange(value: Guid | '') {
    if (!draft) return;

    draft.projectId = value;
    draft.milestoneId = NONE;
    milestones = value ? await projectsApi.milestones(value).catch(() => []) : [];
  }

  function toggleLabel(id: Guid) {
    if (!draft) return;

    draft.labelIds = draft.labelIds.includes(id)
      ? draft.labelIds.filter((labelId) => labelId !== id)
      : [...draft.labelIds, id];
  }

  /* --------------------------------------------------------------- options ---- */

  const projectsOfTeam = $derived(workspace.projects.filter((project) => !project.archivedAt));

  const stateOptions = $derived<SelectOption<Guid>[]>(
    states.map((state) => ({ value: state.id, label: state.name, color: state.color, icon: 'circle-dot' }))
  );

  const priorityOptions = $derived<SelectOption<IssuePriority>[]>(
    PRIORITY_ORDER.map((priority) => ({
      value: priority,
      label: PRIORITY[priority].label,
      icon: PRIORITY[priority].icon,
      color: PRIORITY[priority].color
    }))
  );

  const assigneeOptions = $derived<SelectOption<Guid | ''>[]>([
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

  const projectOptions = $derived<SelectOption<Guid | ''>[]>([
    { value: NONE, label: 'No project', icon: 'folder', color: 'var(--fg-tertiary)' },
    ...projectsOfTeam.map((project) => ({
      value: project.id,
      label: project.name,
      color: project.color
    }))
  ]);

  const milestoneOptions = $derived<SelectOption<Guid | ''>[]>([
    { value: NONE, label: 'No milestone', icon: 'milestone', color: 'var(--fg-tertiary)' },
    ...milestones.map((milestone) => ({
      value: milestone.id,
      label: milestone.name,
      icon: 'milestone' as const
    }))
  ]);

  const estimateOptions = $derived<SelectOption<number>[]>([
    { value: 0, label: 'No estimate', icon: 'minus', color: 'var(--fg-tertiary)' },
    ...estimateScale.map((points) => ({
      value: points,
      label: `${points} ${points === 1 ? 'point' : 'points'}`
    }))
  ]);

  /* ----------------------------------------------------------------- save ---- */

  const dirty = $derived.by(() => {
    if (!draft) return false;
    if (!original) return draft.title.trim().length > 0 || draft.description.trim().length > 0;

    return (
      draft.title !== original.title ||
      draft.description !== original.description ||
      draft.stateId !== original.stateId ||
      draft.priority !== original.priority ||
      draft.assigneeId !== original.assigneeId ||
      draft.projectId !== original.projectId ||
      draft.milestoneId !== original.milestoneId ||
      draft.estimate !== original.estimate ||
      draft.dueDate !== original.dueDate ||
      draft.labelIds.length !== original.labelIds.length ||
      draft.labelIds.some((id) => !original!.labelIds.includes(id))
    );
  });

  /** Only the fields the user actually changed. An absent key is untouched on the server. */
  function diff(): UpdateIssueRequest {
    const changes: UpdateIssueRequest = {};
    if (!draft || !original) return changes;

    if (draft.title !== original.title) changes.title = draft.title.trim();
    if (draft.description !== original.description) changes.description = draft.description.trim() || null;
    if (draft.stateId !== original.stateId) changes.stateId = draft.stateId;
    if (draft.priority !== original.priority) changes.priority = draft.priority;
    if (draft.assigneeId !== original.assigneeId) changes.assigneeId = draft.assigneeId || null;
    if (draft.projectId !== original.projectId) changes.projectId = draft.projectId || null;
    if (draft.milestoneId !== original.milestoneId) changes.milestoneId = draft.milestoneId || null;
    if (draft.estimate !== original.estimate) changes.estimate = draft.estimate || null;
    if (draft.dueDate !== original.dueDate) changes.dueDate = draft.dueDate || null;

    if (
      draft.labelIds.length !== original.labelIds.length ||
      draft.labelIds.some((id) => !original!.labelIds.includes(id))
    ) {
      changes.labelIds = draft.labelIds;
    }

    return changes;
  }

  async function save() {
    if (!draft || !teamId || saving) return;

    if (!draft.title.trim()) {
      fieldErrors = { title: 'A title is required.' };
      titleInput?.focus();
      return;
    }

    saving = true;
    error = null;
    fieldErrors = {};

    try {
      let result: IssueSummary;

      if (target?.issueId) {
        const changes = diff();

        if (Object.keys(changes).length === 0) {
          issueEditor.close();
          return;
        }

        result = await issuesApi.update(target.issueId, changes);
      } else {
        const body: CreateIssueRequest = {
          teamId,
          title: draft.title.trim(),
          description: draft.description.trim() || null,
          stateId: draft.stateId || null,
          priority: draft.priority,
          assigneeId: draft.assigneeId || null,
          projectId: draft.projectId || null,
          milestoneId: draft.milestoneId || null,
          parentId: target?.parentId ?? null,
          estimate: draft.estimate || null,
          dueDate: draft.dueDate || null,
          labelIds: draft.labelIds
        };

        result = await issuesApi.create(body);
      }

      // Applied to open views immediately; the socket's echo that follows is the same upsert.
      announce(target?.issueId ? 'Updated' : 'Created', result);
      toasts.success(target?.issueId ? `${result.key} saved.` : `${result.key} created.`);
      issueEditor.close();
    } catch (failure) {
      if (failure instanceof ApiError) {
        // The API's own wording, which beats anything this form could invent.
        error = failure.message;
        fieldErrors = failure.fieldErrors;
      } else {
        error = 'Saving failed.';
      }
    } finally {
      saving = false;
    }
  }

  /**
   * Archive is the everyday "done with this", and the action an ordinary team member is allowed to
   * take. Deleting outright needs team-lead authority and is deliberately not offered here.
   */
  async function archive() {
    if (!target?.issueId || saving) return;

    const answer = await confirm.ask({
      title: `Archive ${issueKey}?`,
      message: 'It leaves the board and every list. Restoring it later is an API call away.',
      confirmLabel: 'Archive',
      cancelLabel: 'Cancel'
    });

    if (!answer) return;

    saving = true;

    try {
      const result = await issuesApi.archive(target.issueId);
      announce('Archived', result);
      toasts.success(`${result.key} archived.`);
      issueEditor.close();
    } catch (failure) {
      error = failure instanceof ApiError ? failure.message : 'Archiving failed.';
    } finally {
      saving = false;
    }
  }

  async function close() {
    if (saving) return;

    if (dirty) {
      const answer = await confirm.discard(
        editing ? "this issue's unsaved edits" : 'this issue'
      );
      if (!answer) return;
    }

    issueEditor.close();
  }

  /*
   * While the form is open its commands are the keyboard's and the palette's: Ctrl+S saves the issue
   * rather than the page behind it, and Ctrl+K lists what this dialog can do.
   */
  $effect(() => {
    if (!issueEditor.isOpen || !dialog) return;

    commands.modal = {
      element: dialog,
      groups: [
        {
          label: editing ? (issueKey ?? 'Issue') : 'New issue',
          items: [
            {
              label: editing ? 'Save changes' : 'Create issue',
              icon: 'check',
              shortcut: 'mod+s',
              keywords: ['save', 'create', 'submit'],
              // Enabled without a title, so the shortcut says why it cannot save instead of nothing.
              disabled: !draft || loading || saving,
              run: () => void save()
            },
            ...(editing
              ? [
                  {
                    label: 'Archive issue',
                    icon: 'archive' as const,
                    danger: true,
                    disabled: saving,
                    run: () => void archive()
                  }
                ]
              : []),
            {
              label: 'Close',
              icon: 'x',
              keywords: ['cancel', 'discard'],
              disabled: saving,
              run: () => void close()
            }
          ]
        }
      ]
    };

    return () => {
      commands.modal = null;
    };
  });

  /** Ctrl+Enter saves, because a form whose only commit is a mouse click is a form people abandon. */
  function onKeyDown(event: KeyboardEvent) {
    if ((event.ctrlKey || event.metaKey) && event.key === 'Enter') {
      event.preventDefault();
      void save();
    }
  }
</script>

<Modal
  bind:dialog
  open={issueEditor.isOpen}
  title={editing ? (issueKey ?? 'Issue') : 'New issue'}
  subtitle={workspace.teams.find((team) => team.id === teamId)?.name}
  size="l"
  dismissible={!saving}
  onclose={() => void close()}>
  {#if loading}
    <div class="empty"><Icon name="loader-circle" size={20} class="spin" /></div>
  {:else if draft}
    <!-- svelte-ignore a11y_no_static_element_interactions -->
    <div class="form" onkeydown={onKeyDown}>
      {#if error}
        <div class="alert alert-error" role="alert">
          <Icon name="circle-alert" size={15} />
          <span>{error}</span>
        </div>
      {/if}

      <textarea
        bind:this={titleInput}
        bind:value={draft.title}
        class="title-input"
        class:invalid={Boolean(fieldErrors.title)}
        rows="1"
        placeholder="Issue title"
        aria-label="Title"
        disabled={saving}></textarea>
      {#if fieldErrors.title}<p class="field-error">{fieldErrors.title}</p>{/if}

      <textarea
        bind:value={draft.description}
        class="description-input"
        rows="6"
        placeholder="Add a description…"
        aria-label="Description"
        disabled={saving}></textarea>

      <!--
        One wrapping row of property chips, with no caption column. The icon and the words in a chip
        are its label, which only works because every optional property has an explicit empty option.
      -->
      <div class="properties">
        <Select
          options={stateOptions}
          value={draft.stateId}
          onchange={(value) => (draft!.stateId = value)}
          label="Status"
          disabled={saving} />

        <Select
          options={priorityOptions}
          value={draft.priority}
          onchange={(value) => (draft!.priority = value)}
          label="Priority"
          disabled={saving} />

        <Select
          options={assigneeOptions}
          value={draft.assigneeId}
          onchange={(value) => (draft!.assigneeId = value)}
          label="Assignee"
          disabled={saving} />

        <Select
          options={projectOptions}
          value={draft.projectId}
          onchange={(value) => void onProjectChange(value)}
          label="Project"
          disabled={saving} />

        <Select
          options={milestoneOptions}
          value={draft.milestoneId}
          onchange={(value) => (draft!.milestoneId = value)}
          label="Milestone"
          disabled={saving || !draft.projectId} />

        <Select
          options={estimateOptions}
          value={draft.estimate}
          onchange={(value) => (draft!.estimate = value)}
          label="Estimate"
          disabled={saving} />

        <label class="due">
          <Icon name="calendar" size={13} />
          <DateInput
            bind:value={draft.dueDate}
            aria-label="Due date"
            disabled={saving} />
        </label>
      </div>

      {#if labels.length > 0}
        <div class="labels">
          <span class="labels-icon" title="Labels"><Icon name="tag" size={13} /></span>
          {#each labels as label (label.id)}
            {@const on = draft.labelIds.includes(label.id)}
            <button
              type="button"
              class="label-toggle"
              class:on
              style:background={on ? alpha(label.color, 0.16) : undefined}
              style:border-color={alpha(label.color, on ? 0.5 : 0.25)}
              disabled={saving}
              aria-pressed={on}
              onclick={() => toggleLabel(label.id)}>
              <span class="label-dot" style:background={label.color}></span>
              {label.name}
            </button>
          {/each}
        </div>
      {/if}
    </div>
  {/if}

  {#snippet footer()}
    {#if editing}
      <button type="button" class="btn btn-danger" onclick={() => void archive()} disabled={saving}>
        <Icon name="archive" size={14} />
        Archive
      </button>
    {/if}

    <span class="spacer"></span>

    {#if dirty}<span class="dirty">Unsaved changes</span>{/if}

    <button type="button" class="btn" onclick={() => void close()} disabled={saving}>Cancel</button>
    <button
      type="button"
      class="btn btn-primary"
      onclick={() => void save()}
      disabled={saving || loading || !draft?.title.trim()}>
      {#if saving}<Icon name="loader-circle" size={14} class="spin" />{/if}
      {editing ? 'Save changes' : 'Create issue'}
    </button>
  {/snippet}
</Modal>

<style>
  .form {
    display: flex;
    flex-direction: column;
    gap: var(--s-4);
  }

  /* Borderless, because the value is the label: a heading reads as a heading without a caption. */
  .title-input {
    width: 100%;
    padding: 0;
    border: 0;
    border-bottom: 1px solid transparent;
    background: none;
    font-size: var(--text-xl);
    font-weight: 600;
    letter-spacing: -0.01em;
    line-height: var(--leading-tight);
    resize: none;
    field-sizing: content;
  }

  .title-input:focus {
    border-bottom-color: var(--border);
    outline: none;
  }

  .title-input.invalid {
    border-bottom-color: var(--danger);
  }

  .title-input::placeholder,
  .description-input::placeholder {
    color: var(--fg-tertiary);
  }

  .description-input {
    width: 100%;
    min-height: 120px;
    padding: 0;
    border: 0;
    background: none;
    color: var(--fg-secondary);
    line-height: var(--leading-relaxed);
    resize: vertical;
  }

  .description-input:focus {
    outline: none;
  }

  .properties {
    display: flex;
    flex-wrap: wrap;
    gap: var(--s-3);
    padding-top: var(--s-4);
    border-top: 1px solid var(--split);
  }

  .due {
    display: inline-flex;
    align-items: center;
    gap: var(--s-2);
    height: var(--control-h-sm);
    padding: 0 var(--s-3);
    border-radius: var(--radius-sm);
    background: var(--bg-sunken);
    color: var(--fg-tertiary);
    font-size: var(--text-sm);
  }

  .due :global(.date-input) {
    border: 0;
    background: none;
    color: var(--fg);
    font-size: var(--text-sm);
    outline: none;
  }

  .labels {
    display: flex;
    flex-wrap: wrap;
    gap: var(--s-2);
    align-items: center;
  }

  .labels-icon {
    display: inline-flex;
    margin-right: var(--s-2);
    color: var(--fg-tertiary);
  }

  /*
   * Each chip sets its own colours inline, deliberately: being selected must not repaint a label in
   * the accent colour, or every chosen label would look like the same label.
   */
  .label-toggle {
    display: inline-flex;
    align-items: center;
    gap: var(--s-2);
    height: 22px;
    padding: 0 var(--s-3);
    border: 1px solid var(--border);
    border-radius: var(--radius-full);
    background: none;
    color: var(--fg-secondary);
    font-size: var(--text-xs);
    cursor: pointer;
  }

  .label-toggle.on {
    color: var(--fg);
    font-weight: 500;
  }

  .label-dot {
    width: 7px;
    height: 7px;
    border-radius: 50%;
  }

  .dirty {
    margin-right: var(--s-2);
    color: var(--fg-tertiary);
    font-size: var(--text-sm);
  }
</style>
