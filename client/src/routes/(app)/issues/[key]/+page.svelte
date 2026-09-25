<script lang="ts">
  import DateInput from '$components/DateInput.svelte';
  import { page } from '$app/state';
  import { ApiError, issues as issuesApi, projects as projectsApi } from '$lib/api';
  import type {
    AttachmentDto,
    Guid,
    IssueDetail,
    IssuePriority,
    IssueRelationDto,
    LabelDto,
    MilestoneDto,
    TeamMemberDto,
    UpdateIssueRequest,
    WorkflowStateDto
  } from '$lib/api/types';
  import { Permission, session } from '$lib/auth/session.svelte';
  import { chrome } from '$lib/chrome.svelte';
  import { announce, applyChange, isLocalEcho, onIssueChange } from '$lib/issues/changes';
  import { onUpload } from '$lib/markdown/attachments';
  import { issueEditor } from '$lib/issues/editor.svelte';
  import { ESTIMATE_SCALE, PRIORITY, PRIORITY_ORDER } from '$lib/meta';
  import { alpha, formatExact, relativeTime } from '$lib/format';
  import { navigate } from '$lib/navigation.svelte';
  import { realtime } from '$lib/realtime/hub.svelte';
  import { inbox } from '$lib/inbox.svelte';
  import { loadIssueFormData, workspace } from '$lib/workspace.svelte';

  import Avatar from '$components/Avatar.svelte';
  import Icon from '$components/Icon.svelte';
  import Select from '$components/Select.svelte';
  import type { SelectOption } from '$components/select';
  import StateIcon from '$components/StateIcon.svelte';
  import IssueRow from '$components/issues/IssueRow.svelte';
  import EditableMarkdown from '$components/markdown/EditableMarkdown.svelte';
  import IssueComments from '$components/issues/IssueComments.svelte';
  import IssueAttachments from '$components/issues/IssueAttachments.svelte';
  import IssueRelations from '$components/issues/IssueRelations.svelte';
  import IssueActivity from '$components/issues/IssueActivity.svelte';
  import IssueSubscribers from '$components/issues/IssueSubscribers.svelte';
  import { confirm } from '$components/confirm.svelte';
  import { toasts } from '$components/toast.svelte';

  /**
   * One issue, in full.
   *
   * A breadcrumb and a save strip stay above two independently scrolling columns: the issue itself on
   * the left — title, description, sub-issues, relations, the conversation — and its properties,
   * labels and files on the right.
   *
   * Editing follows the same rule as the dialog: the values the page held when it opened are
   * remembered, and saving sends only what actually changed. What is different here is that the page
   * stays open while other people work on the same issue, so a refresh from the socket must not
   * replace the fields someone is halfway through editing, nor the comment they have not posted yet.
   */
  const NONE = '';

  const key = $derived(page.params.key!);

  let issue = $state<IssueDetail | null>(null);
  let loading = $state(true);
  let error = $state<string | null>(null);
  let saving = $state(false);

  let states = $state<WorkflowStateDto[]>([]);
  let members = $state<TeamMemberDto[]>([]);
  let labels = $state<LabelDto[]>([]);
  let milestones = $state<MilestoneDto[]>([]);
  let estimateScale = $state<number[]>(ESTIMATE_SCALE);

  let title = $state('');
  let description = $state('');
  let stateId = $state<Guid>(NONE);
  let priority = $state<IssuePriority>('None');
  let assigneeId = $state<Guid | ''>(NONE);
  let projectId = $state<Guid | ''>(NONE);
  let milestoneId = $state<Guid | ''>(NONE);
  let estimate = $state(0);
  let dueDate = $state('');
  let labelIds = $state<Guid[]>([]);

  let saved = $state({
    title: '',
    description: '',
    stateId: NONE as Guid,
    priority: 'None' as IssuePriority,
    assigneeId: NONE as Guid | '',
    projectId: NONE as Guid | '',
    milestoneId: NONE as Guid | '',
    estimate: 0,
    dueDate: '',
    labelIds: [] as Guid[]
  });

  let hasCommentDraft = $state(false);
  let descriptionUploading = $state(false);
  let descriptionEditor = $state<EditableMarkdown | null>(null);
  let hasAttachmentDraft = $state(false);
  let selectedChildId = $state<string | null>(null);

  /*
   * An archived issue is a record. It opens — the inbox and the feeds link to it — but nothing on it
   * changes until someone restores it, and the server refuses the same writes these flags hide.
   * Following it is still allowed: that is about the reader, not the issue.
   */
  const archived = $derived(Boolean(issue?.archivedAt));
  const canRestore = $derived(archived && session.can(issue?.teamId, Permission.Write));
  const canWrite = $derived(!archived && session.can(issue?.teamId, Permission.Write));
  const canComment = $derived(!archived && session.can(issue?.teamId, Permission.Comment));

  // Destroying an issue is a lead's decision — the team's leads and organisation admins — and it
  // works on an archived issue too: that is where most of them will be deleted from.
  const canDelete = $derived(session.can(issue?.teamId, Permission.Administer));

  /** Set once this page has deleted the issue, so the socket's echo does not try to reload it. */
  let deleted = false;

  /* ----------------------------------------------------------------- load ---- */

  async function load(preserveEdits = false) {
    loading = true;
    error = null;

    const requested = key;

    try {
      const loaded = await issuesApi.byKey(requested);

      // Read from the router rather than `key`: the response may land after this page has been left
      // (archiving navigates away while the socket's echo is still reloading), and a derived of a
      // destroyed component must not be read. Another route has no key, so that load stops here too.
      if (page.params.key !== requested) return;

      issue = loaded;

      const data = await loadIssueFormData(loaded.teamId);
      states = data.states;
      members = data.members;
      labels = data.labels;
      milestones = loaded.projectId
        ? await projectsApi.milestones(loaded.projectId).catch(() => [])
        : [];

      estimateScale =
        loaded.estimate && !ESTIMATE_SCALE.includes(loaded.estimate)
          ? [...ESTIMATE_SCALE, loaded.estimate].sort((a, b) => a - b)
          : ESTIMATE_SCALE;

      const next = {
        title: loaded.title,
        description: loaded.description ?? '',
        stateId: loaded.stateId,
        priority: loaded.priority,
        assigneeId: loaded.assignee?.id ?? NONE,
        projectId: loaded.projectId ?? NONE,
        milestoneId: loaded.milestoneId ?? NONE,
        estimate: loaded.estimate ?? 0,
        dueDate: loaded.dueDate ?? '',
        labelIds: loaded.labels.map((label) => label.id)
      };

      // A collaboration refresh knows what the server now holds without claiming the fields back:
      // the baseline moves so the diff stays honest, and only untouched fields follow it.
      const keep = preserveEdits;

      if (!keep || title === saved.title) title = next.title;
      if (!keep || description === saved.description) description = next.description;
      if (!keep || stateId === saved.stateId) stateId = next.stateId;
      if (!keep || priority === saved.priority) priority = next.priority;
      if (!keep || assigneeId === saved.assigneeId) assigneeId = next.assigneeId;
      if (!keep || projectId === saved.projectId) projectId = next.projectId;
      if (!keep || milestoneId === saved.milestoneId) milestoneId = next.milestoneId;
      if (!keep || estimate === saved.estimate) estimate = next.estimate;
      if (!keep || dueDate === saved.dueDate) dueDate = next.dueDate;
      if (!keep || sameLabels(labelIds, saved.labelIds)) labelIds = [...next.labelIds];

      saved = { ...next, labelIds: [...next.labelIds] };
    } catch (failure) {
      error =
        failure instanceof ApiError
          ? failure.isNotFound
            ? `${requested} does not exist, or you cannot see it.`
            : failure.message
          : 'Could not load that issue.';
    } finally {
      loading = false;
    }
  }

  const sameLabels = (a: Guid[], b: Guid[]) =>
    a.length === b.length && a.every((id) => b.includes(id));

  $effect(() => {
    void key;
    void load();
  });

  $effect(() =>
    onIssueChange((change) => {
      const current = issue;
      if (!current) return;

      // A sub-issue: one just filed from the Sub-issue button, one someone else added, or one that
      // has been reparented, archived or deleted. Applied to the list in place — the whole detail
      // does not have to be refetched to gain or lose a row, and the id being *different* from this
      // issue's is exactly what makes it a child rather than a reason to ignore it.
      if (current.children.some((child) => child.id === change.id) || change.entity?.parentId === current.id) {
        issue = {
          ...current,
          children: applyChange(current.children, change, (child) => child.parentId === current.id)
        };
        return;
      }

      if (change.id !== current.id) return;

      // An echo of this page's own save is already on screen. Anything else — including this user in
      // another tab — is news, and the detail carries children, relations and attachments that a
      // summary does not, so it is reloaded rather than patched.
      if (!isLocalEcho(change) && !deleted) void load(true);
    })
  );

  $effect(() => realtime.onReconnected(() => void load(true)));

  // Everything in the inbox about this issue is on screen now — including what arrives while it is.
  const openIssueId = $derived(issue?.id);

  $effect(() => {
    if (openIssueId) void inbox.readIssue(openIssueId);
  });

  $effect(() =>
    realtime.on('NotificationChanged', (change) => {
      if (openIssueId && change.issueId === openIssueId) void inbox.readIssue(openIssueId);
    })
  );

  // Images pasted into the description or a comment land on the issue straight away.
  $effect(() =>
    onUpload((issueId, attachment) => {
      const current = issue;
      if (!current || current.id !== issueId) return;
      if (current.attachments.some((existing) => existing.id === attachment.id)) return;

      issue = { ...current, attachments: [...current.attachments, attachment] };
    })
  );

  /* ----------------------------------------------------------------- save ---- */

  const dirty = $derived(
    title !== saved.title ||
      description !== saved.description ||
      stateId !== saved.stateId ||
      priority !== saved.priority ||
      assigneeId !== saved.assigneeId ||
      projectId !== saved.projectId ||
      milestoneId !== saved.milestoneId ||
      estimate !== saved.estimate ||
      dueDate !== saved.dueDate ||
      !sameLabels(labelIds, saved.labelIds)
  );

  function diff(): UpdateIssueRequest {
    const changes: UpdateIssueRequest = {};

    if (title !== saved.title) changes.title = title.trim();
    if (description !== saved.description) changes.description = description.trim() || null;
    if (stateId !== saved.stateId) changes.stateId = stateId;
    if (priority !== saved.priority) changes.priority = priority;
    if (assigneeId !== saved.assigneeId) changes.assigneeId = assigneeId || null;
    if (projectId !== saved.projectId) changes.projectId = projectId || null;
    if (milestoneId !== saved.milestoneId) changes.milestoneId = milestoneId || null;
    if (estimate !== saved.estimate) changes.estimate = estimate || null;
    if (dueDate !== saved.dueDate) changes.dueDate = dueDate || null;
    if (!sameLabels(labelIds, saved.labelIds)) changes.labelIds = labelIds;

    return changes;
  }

  async function save() {
    if (!issue || saving || descriptionUploading || !dirty) return;

    if (!title.trim()) {
      toasts.error('An issue needs a title.');
      return;
    }

    saving = true;

    try {
      const result = await issuesApi.update(issue.id, diff());

      announce('Updated', result);
      saved = {
        title,
        description,
        stateId,
        priority,
        assigneeId,
        projectId,
        milestoneId,
        estimate,
        dueDate,
        labelIds: [...labelIds]
      };

      toasts.success(`${result.key} saved.`);
    } catch (failure) {
      toasts.error(failure instanceof ApiError ? failure.message : 'Saving failed.');
    } finally {
      saving = false;
    }
  }

  function revert() {
    title = saved.title;
    description = saved.description;
    stateId = saved.stateId;
    priority = saved.priority;
    assigneeId = saved.assigneeId;
    projectId = saved.projectId;
    milestoneId = saved.milestoneId;
    estimate = saved.estimate;
    dueDate = saved.dueDate;
    labelIds = [...saved.labelIds];
  }

  async function archive() {
    if (!issue) return;

    const answer = await confirm.ask({
      title: `Archive ${issue.key}?`,
      message: 'It leaves the board and every list. Its comments, files and history stay.',
      confirmLabel: 'Archive',
      cancelLabel: 'Cancel'
    });

    if (!answer) return;

    try {
      announce('Archived', await issuesApi.archive(issue.id));
      toasts.success(`${issue.key} archived.`);
      await navigate('/my-issues', { force: true });
    } catch (failure) {
      toasts.error(failure instanceof ApiError ? failure.message : 'Archiving failed.');
    }
  }

  async function remove() {
    if (!issue || !canDelete) return;

    const target = issue;
    const children = target.children.length;

    const answer = await confirm.ask({
      title: `Delete ${target.key} permanently?`,
      message:
        'Its comments, files and relations are deleted with it, and so are the inbox entries about it. ' +
        (children > 0
          ? `Its ${children === 1 ? 'sub-issue becomes' : `${children} sub-issues become`} top-level issues. `
          : '') +
        'Its activity history is kept. This cannot be undone.' +
        (dirty ? ' Unsaved changes will be discarded.' : ''),
      requiredText: target.key,
      confirmLabel: 'Delete issue',
      cancelLabel: 'Cancel',
      danger: true
    });

    if (!answer || issue?.id !== target.id) return;

    try {
      deleted = true;
      await issuesApi.remove(target.id);
      toasts.success(`${target.key} deleted.`);
      await navigate('/my-issues', { force: true });
    } catch (failure) {
      deleted = false;
      toasts.error(failure instanceof ApiError ? failure.message : 'Deleting failed.');
    }
  }

  async function restore() {
    if (!issue || !canRestore) return;

    try {
      const result = await issuesApi.restore(issue.id);
      announce('Restored', result);
      toasts.success(`${result.key} restored.`);
      await load(true);
    } catch (failure) {
      toasts.error(failure instanceof ApiError ? failure.message : 'Restoring failed.');
    }
  }

  async function onProjectChange(value: Guid | '') {
    projectId = value;
    milestoneId = NONE;
    milestones = value ? await projectsApi.milestones(value).catch(() => []) : [];
  }

  function toggleLabel(id: Guid) {
    labelIds = labelIds.includes(id) ? labelIds.filter((value) => value !== id) : [...labelIds, id];
  }

  /* ---------------------------------------------------------------- chrome ---- */

  $effect(() => {
    chrome.set({
      title: issue?.key ?? key,
      subtitle: issue?.stateName,
      status: issue
        ? `Updated ${relativeTime(issue.updatedAt)} · ${issue.commentCount} comments`
        : undefined,
      actions: toolbar,
      commands: [
        {
          label: 'Edit description',
          icon: 'pencil',
          shortcut: 'e',
          keywords: ['write', 'markdown', 'body'],
          disabled: !canWrite,
          run: () => descriptionEditor?.edit()
        },
        {
          label: 'Save changes',
          icon: 'check',
          shortcut: 'mod+s',
          disabled: !canWrite || saving || !dirty,
          run: () => void save()
        },
        {
          label: 'Revert changes',
          icon: 'refresh-cw',
          keywords: ['discard', 'undo'],
          disabled: saving || !dirty,
          run: revert
        },
        {
          label: 'New sub-issue',
          icon: 'corner-down-right',
          shortcut: 'shift+c',
          keywords: ['child', 'create'],
          disabled: !canWrite || !issue,
          run: () =>
            issue &&
            issueEditor.create({ teamId: issue.teamId, parentId: issue.id, projectId: issue.projectId })
        },
        ...(issue?.parentKey
          ? [
              {
                label: `Go to parent ${issue.parentKey}`,
                icon: 'arrow-left' as const,
                shortcut: 'g p',
                run: () => void navigate(`/issues/${issue!.parentKey}`)
              }
            ]
          : []),
        archived
          ? {
              label: 'Restore issue',
              icon: 'archive-restore' as const,
              keywords: ['unarchive'],
              disabled: !canRestore,
              run: () => void restore()
            }
          : {
              label: 'Archive issue',
              icon: 'archive' as const,
              danger: true,
              disabled: !canWrite || !issue,
              run: () => void archive()
            },
        {
          label: 'Delete issue',
          icon: 'trash-2',
          keywords: ['remove', 'destroy', 'permanently'],
          danger: true,
          disabled: !canDelete || !issue,
          run: () => void remove()
        }
      ]
    });

    chrome.refresh = () => load(true);
    chrome.busy = loading || saving;

    // What stands to be lost, in the words the prompt will use.
    chrome.unsavedWork = () => {
      const losses: string[] = [];
      if (dirty || descriptionUploading) losses.push("this issue's edits");
      if (hasCommentDraft) losses.push('an unposted comment');
      if (hasAttachmentDraft) losses.push('an unfinished attachment link');

      return losses.length > 0 ? losses.join(' and ') : null;
    };

    return () => chrome.clear();
  });

  /* --------------------------------------------------------------- options ---- */

  const stateOptions = $derived<SelectOption<Guid>[]>(
    states.map((state) => ({ value: state.id, label: state.name, color: state.color, icon: 'circle-dot' }))
  );

  const priorityOptions = $derived<SelectOption<IssuePriority>[]>(
    PRIORITY_ORDER.map((value) => ({
      value,
      label: PRIORITY[value].label,
      icon: PRIORITY[value].icon,
      color: PRIORITY[value].color
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
    ...workspace.projects
      .filter((project) => !project.archivedAt)
      .map((project) => ({ value: project.id, label: project.name, color: project.color }))
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
    ...estimateScale.map((points) => ({ value: points, label: `${points} points` }))
  ]);
</script>

{#snippet toolbar()}
  {#if archived}
    <button type="button" class="btn btn-sm btn-primary" onclick={() => void restore()} disabled={!canRestore}>
      <Icon name="archive-restore" size={13} />
      Restore
    </button>
  {:else}
  {#if dirty}
    <span class="dirty">Unsaved changes</span>
    <button type="button" class="btn btn-sm" onclick={revert} disabled={saving || descriptionUploading}>Revert</button>
  {/if}

  <button
    type="button"
    class="btn btn-sm btn-primary"
    onclick={() => void save()}
    disabled={!canWrite || saving || descriptionUploading || !dirty}>
    {saving ? 'Saving…' : 'Save changes'}
  </button>

  <button
    type="button"
    class="btn btn-sm"
    onclick={() => issue && issueEditor.create({ teamId: issue.teamId, parentId: issue.id, projectId: issue.projectId })}
    disabled={!canWrite}>
    <Icon name="corner-down-right" size={13} />
    Sub-issue
  </button>
  {/if}
{/snippet}

{#if error}
  <div class="empty">
    <Icon name="circle-alert" size={28} />
    <p class="empty-title">{error}</p>
    <button type="button" class="btn btn-sm" onclick={() => void navigate('/my-issues')}>
      Back to My Issues
    </button>
  </div>
{:else if loading && !issue}
  <div class="empty"><Icon name="loader-circle" size={20} class="spin" /></div>
{:else if issue}
  <div class="detail">
    <div class="main">
      <nav class="breadcrumb">
        <button type="button" class="crumb" onclick={() => void navigate('/board')}>
          {workspace.teams.find((team) => team.id === issue!.teamId)?.name ?? 'Team'}
        </button>
        {#if issue.projectId && issue.projectName}
          <Icon name="chevron-right" size={12} />
          <button type="button" class="crumb" onclick={() => void navigate(`/projects/${issue!.projectId}`)}>
            {issue.projectName}
          </button>
        {/if}
        {#if issue.parentKey}
          <Icon name="chevron-right" size={12} />
          <button type="button" class="crumb" onclick={() => void navigate(`/issues/${issue!.parentKey}`)}>
            {issue.parentKey}
          </button>
        {/if}
        <Icon name="chevron-right" size={12} />
        <span class="issue-key">{issue.key}</span>

        {#if issue.archivedAt}<span class="chip">Archived</span>{/if}
      </nav>

      {#if archived}
        <div class="alert archived-note">
          <Icon name="archive" size={15} />
          <span>
            Archived {relativeTime(issue.archivedAt)}. It can be read, but not changed or followed{canRestore
              ? ' until it is restored'
              : ''}.
          </span>
          {#if canRestore}
            <button type="button" class="btn btn-sm" onclick={() => void restore()}>Restore</button>
          {/if}
        </div>
      {/if}

      <textarea
        bind:value={title}
        class="title"
        rows="1"
        aria-label="Title"
        readonly={!canWrite}
        placeholder="Issue title"></textarea>

      <EditableMarkdown
        bind:this={descriptionEditor} bind:uploading={descriptionUploading}
        bind:value={description}
        canEdit={canWrite}
        issueId={issue.id}
        rows={10} />

      {#if issue.children.length > 0}
        <section class="sub-issues">
          <header class="section-header">
            <h2>Sub-issues</h2>
            <span class="badge">{issue.children.length}</span>
          </header>

          <div class="rows">
            {#each issue.children as child (child.id)}
              <IssueRow
                issue={child}
                selected={selectedChildId === child.id}
                onselect={(selected) => (selectedChildId = selected.id)}
                onopen={(selected) => void navigate(`/issues/${selected.key}`)} />
            {/each}
          </div>
        </section>
      {/if}

      <IssueRelations
        issueId={issue.id}
        relations={issue.relations}
        canEdit={canWrite}
        onchange={(next: IssueRelationDto[]) => (issue = { ...issue!, relations: next })} />

      <IssueActivity issueId={issue.id} />

      <IssueComments
        issueId={issue.id}
        {canComment}
        ondraft={(has) => (hasCommentDraft = has)} />
    </div>

    <aside class="side">
      <div class="property">
        <p class="caption">Status</p>
        <Select
          options={stateOptions}
          value={stateId}
          onchange={(value) => (stateId = value)}
          variant="field"
          disabled={!canWrite}
          label="Status" />
      </div>

      <div class="property">
        <p class="caption">Priority</p>
        <Select
          options={priorityOptions}
          value={priority}
          onchange={(value) => (priority = value)}
          variant="field"
          disabled={!canWrite}
          label="Priority" />
      </div>

      <div class="property">
        <p class="caption">Assignee</p>
        <Select
          options={assigneeOptions}
          value={assigneeId}
          onchange={(value) => (assigneeId = value)}
          variant="field"
          disabled={!canWrite}
          label="Assignee" />
      </div>

      <div class="property">
        <p class="caption">Project</p>
        <Select
          options={projectOptions}
          value={projectId}
          onchange={(value) => void onProjectChange(value)}
          variant="field"
          disabled={!canWrite}
          label="Project" />
      </div>

      <div class="property">
        <p class="caption">Milestone</p>
        <Select
          options={milestoneOptions}
          value={milestoneId}
          onchange={(value) => (milestoneId = value)}
          variant="field"
          disabled={!canWrite || !projectId}
          label="Milestone" />
      </div>

      <div class="property">
        <p class="caption">Estimate</p>
        <Select
          options={estimateOptions}
          value={estimate}
          onchange={(value) => (estimate = value)}
          variant="field"
          disabled={!canWrite}
          label="Estimate" />
      </div>

      <div class="property">
        <p class="caption">Due date</p>
        <DateInput bind:value={dueDate} class="input" disabled={!canWrite} aria-label="Due date" />
      </div>

      <div class="property">
        <p class="caption">Labels</p>
        <div class="labels">
          {#each labels as label (label.id)}
            {@const on = labelIds.includes(label.id)}
            <button
              type="button"
              class="label-toggle"
              class:on
              style:background={on ? alpha(label.color, 0.16) : undefined}
              style:border-color={alpha(label.color, on ? 0.5 : 0.25)}
              disabled={!canWrite}
              aria-pressed={on}
              onclick={() => toggleLabel(label.id)}>
              <span class="label-dot" style:background={label.color}></span>
              {label.name}
            </button>
          {:else}
            <p class="muted small">This team has no labels.</p>
          {/each}
        </div>
      </div>

      <hr />

      <IssueSubscribers issueId={issue.id} teamId={issue.teamId} {archived} />

      <hr />

      <IssueAttachments
        issueId={issue.id}
        attachments={issue.attachments}
        canAttach={canComment}
        onchange={(next: AttachmentDto[]) => (issue = { ...issue!, attachments: next })}
        ondraft={(has) => (hasAttachmentDraft = has)} />

      <hr />

      <dl class="facts">
        <dt>State</dt>
        <dd>
          <StateIcon type={issue.stateType} color={issue.stateColor} size={12} />
          {issue.stateName}
        </dd>

        <dt>Created</dt>
        <dd title={formatExact(issue.createdAt)}>
          <Avatar name={issue.creator.displayName} seed={issue.creator.email} size={16} />
          {issue.creator.displayName}, {relativeTime(issue.createdAt)}
        </dd>

        <dt>Updated</dt>
        <dd title={formatExact(issue.updatedAt)}>{relativeTime(issue.updatedAt)}</dd>

        {#if issue.startedAt}
          <dt>Started</dt>
          <dd title={formatExact(issue.startedAt)}>{relativeTime(issue.startedAt)}</dd>
        {/if}
        {#if issue.completedAt}
          <dt>Completed</dt>
          <dd title={formatExact(issue.completedAt)}>{relativeTime(issue.completedAt)}</dd>
        {/if}
      </dl>

      {#if canWrite && !issue.archivedAt}
        <button type="button" class="btn btn-danger btn-sm btn-block" onclick={() => void archive()}>
          <Icon name="archive" size={13} />
          Archive issue
        </button>
      {/if}

      {#if canDelete}
        <button type="button" class="btn btn-danger btn-sm btn-block" onclick={() => void remove()}>
          <Icon name="trash-2" size={13} />
          Delete issue
        </button>
      {/if}
    </aside>
  </div>
{/if}

<style>
  /* Two columns that scroll independently: the conversation is long and the properties must not
     disappear off the top of it. */
  .detail {
    display: grid;
    grid-template-columns: minmax(0, 1fr) 280px;
    height: 100%;
  }

  .main {
    display: flex;
    flex-direction: column;
    gap: var(--s-6);
    min-width: 0;
    padding: var(--s-6) var(--s-7) var(--s-10);
    overflow-y: auto;
  }

  .side {
    display: flex;
    flex-direction: column;
    gap: var(--s-4);
    padding: var(--s-6) var(--s-5) var(--s-10);
    border-left: 1px solid var(--border);
    background: var(--bg-app);
    overflow-y: auto;
  }

  .breadcrumb {
    display: flex;
    flex-wrap: wrap;
    align-items: center;
    gap: var(--s-2);
    color: var(--fg-tertiary);
    font-size: var(--text-sm);
  }

  .crumb {
    padding: 0;
    border: 0;
    background: none;
    color: var(--fg-tertiary);
    cursor: pointer;
  }

  .crumb:hover {
    color: var(--accent);
    text-decoration: underline;
  }

  .title {
    /* Keep the full title height when the scrollable column runs out of space. */
    flex-shrink: 0;
    min-width: 0;
    min-height: 1lh;
    width: 100%;
    padding: 0;
    border: 0;
    border-bottom: 1px solid transparent;
    background: none;
    font-size: var(--text-2xl);
    font-weight: 600;
    letter-spacing: -0.015em;
    line-height: var(--leading-tight);
    overflow-wrap: anywhere;
    resize: none;
    field-sizing: content;
  }

  .title:focus {
    border-bottom-color: var(--border);
    outline: none;
  }

  .title::placeholder {
    color: var(--fg-tertiary);
  }

  .sub-issues {
    display: flex;
    flex-direction: column;
    gap: var(--s-3);
  }

  h2 {
    font-size: var(--text-md);
  }

  .rows {
    margin: 0 calc(var(--s-7) * -1);
    border-top: 1px solid var(--split);
    border-bottom: 1px solid var(--split);
  }

  .property {
    display: flex;
    flex-direction: column;
    gap: var(--s-2);
  }

  .labels {
    display: flex;
    flex-wrap: wrap;
    gap: var(--s-2);
  }

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

  .facts {
    display: grid;
    grid-template-columns: auto minmax(0, 1fr);
    gap: var(--s-2) var(--s-4);
    font-size: var(--text-sm);
  }

  .facts dt {
    color: var(--fg-tertiary);
  }

  .facts dd {
    display: flex;
    align-items: center;
    gap: var(--s-2);
    min-width: 0;
  }

  .archived-note {
    align-items: center;
  }

  .archived-note span {
    flex: 1;
  }

  .dirty {
    margin-right: var(--s-2);
    color: var(--fg-tertiary);
    font-size: var(--text-sm);
  }

  .small {
    font-size: var(--text-sm);
  }

  @media (width <= 1000px) {
    .detail {
      grid-template-columns: minmax(0, 1fr);
      overflow-y: auto;
    }

    .main,
    .side {
      overflow: visible;
    }

    .side {
      border-left: 0;
      border-top: 1px solid var(--border);
    }
  }
</style>
