<script lang="ts">
  import { ApiError, labels as labelsApi, teams as teamsApi } from '$lib/api';
  import type { Guid, LabelDto } from '$lib/api/types';
  import { session } from '$lib/auth/session.svelte';
  import { realtime } from '$lib/realtime/hub.svelte';
  import { workspace } from '$lib/workspace.svelte';
  import ColorPicker from '$components/ColorPicker.svelte';
  import Icon from '$components/Icon.svelte';
  import LabelChip from '$components/LabelChip.svelte';
  import { confirm } from '$components/confirm.svelte';
  import { toasts } from '$components/toast.svelte';

  /**
   * A team's labels, edited one at a time.
   *
   * Unlike the settings and membership above it, a label saves on its own Save rather than on the
   * page's: a label is a small thing that is either right or not, and folding a list of them into the
   * page's dirty state would make "Unsaved changes" mean something nobody could find.
   *
   * Organisation-wide labels are listed too, because they are part of what this team can use. Only
   * administrators may create or change those, and changing one changes it for every team.
   */
  interface Props {
    teamId: Guid;
    canAdminister: boolean;
  }

  let { teamId, canAdminister }: Props = $props();

  const DEFAULT_COLOR = '#95A2B3';

  let all = $state<LabelDto[]>([]);
  let loading = $state(true);
  let busy = $state(false);

  /** The label being edited, or one of the two add rows. Only one at a time. */
  let editing = $state<Guid | 'new' | 'new-org' | null>(null);
  let name = $state('');
  let description = $state('');
  let color = $state(DEFAULT_COLOR);
  let error = $state<string | null>(null);
  let fieldErrors = $state<Record<string, string>>({});

  const teamLabels = $derived(all.filter((label) => label.teamId === teamId));
  const orgLabels = $derived(all.filter((label) => label.teamId === null));

  const mayEdit = (label: LabelDto) => (label.teamId ? canAdminister : session.isAdmin);

  async function load() {
    loading = true;

    try {
      all = await teamsApi.labels(teamId);
    } catch (failure) {
      error = failure instanceof ApiError ? failure.message : 'Could not load the labels.';
    } finally {
      loading = false;
    }
  }

  $effect(() => {
    void teamId;
    editing = null;
    error = null;
    void load();
  });

  // Someone else's edit shows up here without a refresh, unless it would pull a half-typed edit away.
  $effect(() =>
    realtime.on('LabelChanged', (change) => {
      if (change.teamId === teamId && !editing) void load();
    })
  );

  function edit(target: LabelDto | 'new' | 'new-org') {
    const label = typeof target === 'string' ? null : target;

    editing = label?.id ?? (target as 'new' | 'new-org');
    name = label?.name ?? '';
    description = label?.description ?? '';
    color = label?.color ?? DEFAULT_COLOR;
    error = null;
    fieldErrors = {};
  }

  function cancel() {
    editing = null;
    error = null;
    fieldErrors = {};
  }

  async function save() {
    if (busy || !editing) return;

    fieldErrors = {};
    error = null;

    if (!name.trim()) {
      fieldErrors = { name: 'A name is required.' };
      return;
    }

    busy = true;

    try {
      const body = { name: name.trim(), color, description: description.trim() || null };

      if (editing === 'new') {
        const created = await teamsApi.createLabel(teamId, body);
        all = [...all, created];
      } else if (editing === 'new-org') {
        const created = await labelsApi.create(body);
        all = [...all, created];
        forget(created);
      } else {
        const updated = await labelsApi.update(editing, body);
        all = all.map((label) => (label.id === updated.id ? updated : label));
        forget(updated);
      }

      all = sort(all);
      workspace.invalidate(teamId);
      editing = null;
    } catch (failure) {
      if (failure instanceof ApiError) {
        error = failure.message;
        fieldErrors = failure.fieldErrors;
      } else {
        error = 'Saving the label failed.';
      }
    } finally {
      busy = false;
    }
  }

  async function remove(label: LabelDto) {
    const answer = await confirm.ask({
      title: `Delete ${label.name}?`,
      message: label.teamId
        ? 'It comes off every issue that carries it. The issues themselves stay.'
        : 'It is organisation-wide: it comes off every issue, in every team, that carries it. The issues themselves stay.',
      confirmLabel: 'Delete',
      cancelLabel: 'Cancel',
      danger: true
    });

    if (!answer) return;

    busy = true;

    try {
      await labelsApi.remove(label.id);
      all = all.filter((candidate) => candidate.id !== label.id);
      workspace.invalidate(teamId);
      forget(label);
      if (editing === label.id) editing = null;
    } catch (failure) {
      toasts.error(failure instanceof ApiError ? failure.message : 'Deleting the label failed.');
    } finally {
      busy = false;
    }
  }

  /** Organisation labels are cached under every team, and the server announces no change to them. */
  function forget(label: LabelDto) {
    if (label.teamId === null) workspace.invalidateLabels();
  }

  const sort = (labels: LabelDto[]) =>
    [...labels].sort(
      (a, b) => Number(a.teamId !== null) - Number(b.teamId !== null) || a.name.localeCompare(b.name)
    );

  function onKeydown(event: KeyboardEvent) {
    if (event.key === 'Enter' && !event.shiftKey) {
      event.preventDefault();
      void save();
    } else if (event.key === 'Escape') {
      event.preventDefault();
      event.stopPropagation();
      cancel();
    }
  }
</script>

{#snippet editor()}
  <!-- svelte-ignore a11y_no_static_element_interactions -->
  <div class="editor" onkeydown={onKeydown}>
    {#if error}
      <div class="alert alert-error"><Icon name="circle-alert" size={15} /><span>{error}</span></div>
    {/if}

    <div class="grid-2">
      <div class="field">
        <label for="label-name">Name</label>
        <!-- svelte-ignore a11y_autofocus -->
        <input
          id="label-name"
          bind:value={name}
          class="input"
          class:invalid={Boolean(fieldErrors.name)}
          maxlength="60"
          placeholder="Bug"
          autofocus
          disabled={busy} />
        {#if fieldErrors.name}<p class="field-error">{fieldErrors.name}</p>{/if}
      </div>

      <div class="field">
        <label for="label-description">Description</label>
        <input
          id="label-description"
          bind:value={description}
          class="input"
          class:invalid={Boolean(fieldErrors.description)}
          maxlength="500"
          placeholder="Optional — shown when hovering the label"
          disabled={busy} />
        {#if fieldErrors.description}<p class="field-error">{fieldErrors.description}</p>{/if}
      </div>
    </div>

    <div class="field">
      <span class="field-label">Colour</span>
      <ColorPicker value={color} onchange={(value) => (color = value)} disabled={busy} />
      {#if fieldErrors.color}<p class="field-error">{fieldErrors.color}</p>{/if}
    </div>

    <div class="editor-actions">
      <LabelChip label={{ id: '', teamId: null, name: name.trim() || 'Label', color, description: null }} />
      <span class="spacer"></span>
      <button type="button" class="btn btn-sm" onclick={cancel} disabled={busy}>Cancel</button>
      <button type="button" class="btn btn-sm btn-primary" onclick={() => void save()} disabled={busy || !name.trim()}>
        {editing === 'new' ? 'Create label' : editing === 'new-org' ? 'Create for every team' : 'Save label'}
      </button>
    </div>
  </div>
{/snippet}

{#snippet row(label: LabelDto)}
  {#if editing === label.id}
    <li>{@render editor()}</li>
  {:else}
    <li class="row">
      <LabelChip {label} />
      <span class="muted truncate description">{label.description ?? ''}</span>
      {#if mayEdit(label)}
        <button
          type="button"
          class="btn btn-quiet btn-icon btn-sm"
          onclick={() => edit(label)}
          disabled={busy || editing !== null}
          aria-label="Edit {label.name}">
          <Icon name="pencil" size={13} />
        </button>
        <button
          type="button"
          class="btn btn-quiet btn-icon btn-sm"
          onclick={() => void remove(label)}
          disabled={busy || editing !== null}
          aria-label="Delete {label.name}">
          <Icon name="x" size={13} />
        </button>
      {/if}
    </li>
  {/if}
{/snippet}

<section class="panel">
  <div class="panel-title">
    <span>Labels</span>
    <span class="badge">{teamLabels.length}</span>
  </div>

  {#if loading && all.length === 0}
    <p class="muted">Loading…</p>
  {:else}
    {#if teamLabels.length === 0 && editing !== 'new'}
      <p class="muted">This team has no labels of its own yet.</p>
    {:else}
      <ul class="labels">
        {#each teamLabels as label (label.id)}
          {@render row(label)}
        {/each}
      </ul>
    {/if}

    {#if editing === 'new'}
      {@render editor()}
    {:else if canAdminister}
      <div class="add">
        <button type="button" class="btn btn-sm" onclick={() => edit('new')} disabled={busy || editing !== null}>
          <Icon name="plus" size={13} />
          New label
        </button>
      </div>
    {/if}

    {#if orgLabels.length > 0 || session.isAdmin}
      <div class="org">
        <p class="muted org-title">
          <Icon name="tag" size={12} />
          Organisation-wide — usable by every team{session.isAdmin ? '' : ', managed by administrators'}
        </p>
        {#if orgLabels.length > 0}
          <ul class="labels">
            {#each orgLabels as label (label.id)}
              {@render row(label)}
            {/each}
          </ul>
        {/if}

        {#if editing === 'new-org'}
          {@render editor()}
        {:else if session.isAdmin}
          <div class="add-org">
            <button type="button" class="btn btn-sm" onclick={() => edit('new-org')} disabled={busy || editing !== null}>
              <Icon name="plus" size={13} />
              New organisation label
            </button>
          </div>
        {/if}
      </div>
    {/if}
  {/if}
</section>

<style>
  .labels {
    display: flex;
    flex-direction: column;
    margin: 0;
    padding: 0;
    list-style: none;
  }

  .row {
    display: flex;
    align-items: center;
    gap: var(--s-3);
    min-height: var(--row-h);
    border-bottom: 1px solid var(--split);
  }

  .row:last-child {
    border-bottom: 0;
  }

  .description {
    flex: 1;
    min-width: 0;
    font-size: var(--text-sm);
  }

  .editor {
    display: flex;
    flex-direction: column;
    gap: var(--s-4);
    margin: var(--s-2) 0;
    padding: var(--s-4);
    border: 1px solid var(--border);
    border-radius: var(--radius-sm);
    background: var(--bg-app);
  }

  .editor-actions {
    display: flex;
    align-items: center;
    gap: var(--s-3);
  }

  .spacer {
    flex: 1;
  }

  .add {
    padding-top: var(--s-4);
    border-top: 1px solid var(--split);
  }

  .org {
    padding-top: var(--s-4);
    border-top: 1px solid var(--split);
  }

  .add-org {
    padding-top: var(--s-2);
  }

  .org-title {
    display: flex;
    align-items: center;
    gap: var(--s-2);
    margin-bottom: var(--s-2);
    font-size: var(--text-xs);
  }
</style>
