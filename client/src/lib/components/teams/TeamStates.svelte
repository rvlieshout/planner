<script lang="ts">
  import { ApiError, teams as teamsApi } from '$lib/api';
  import { WORKFLOW_STATE_TYPES, type Guid, type WorkflowStateDto, type WorkflowStateType } from '$lib/api/types';
  import { STATE_TYPE } from '$lib/meta';
  import { compareRank, rankAt } from '$lib/rank';
  import { realtime } from '$lib/realtime/hub.svelte';
  import { workspace } from '$lib/workspace.svelte';
  import ColorPicker from '$components/ColorPicker.svelte';
  import Icon from '$components/Icon.svelte';
  import Select from '$components/Select.svelte';
  import type { SelectOption } from '$components/select';
  import { confirm } from '$components/confirm.svelte';
  import { toasts } from '$components/toast.svelte';

  /**
   * A team's workflow states — the columns of its board, in board order.
   *
   * Saved one at a time, on the state's own Save, for the same reason labels are: a column is either
   * right or not, and it would be strange for "Unsaved changes" on the page to mean a column nobody
   * can see is being edited.
   *
   * A state's name is what people read; its type is what the application reads. The type decides
   * whether an issue there counts as done in a rollup, which states the board stacks into one lane,
   * and where My Issues groups it — so the editor says so, rather than leaving it to be discovered.
   */
  interface Props {
    teamId: Guid;
    canAdminister: boolean;
  }

  let { teamId, canAdminister }: Props = $props();

  const DEFAULT_COLOR = '#95A2B3';

  const TYPE_HINT: Record<WorkflowStateType, string> = {
    Backlog: 'Not planned yet. Stacked under Todo on the board.',
    Unstarted: 'Planned, not started. Stacked over Backlog on the board.',
    Started: 'Being worked on. Counts as in progress in a rollup.',
    Completed: 'Done. Counts as completed in a rollup.',
    Canceled: 'Dropped. Left out of a rollup altogether.'
  };

  const typeOptions: SelectOption<WorkflowStateType>[] = WORKFLOW_STATE_TYPES.map((type) => ({
    value: type,
    label: STATE_TYPE[type].label,
    icon: STATE_TYPE[type].icon,
    color: STATE_TYPE[type].color
  }));

  let states = $state<WorkflowStateDto[]>([]);
  let loading = $state(true);
  let busy = $state(false);

  /** The state being edited, or the add row. Only one at a time. */
  let editing = $state<Guid | 'new' | null>(null);
  let name = $state('');
  let type = $state<WorkflowStateType>('Unstarted');
  let color = $state(DEFAULT_COLOR);
  let isDefault = $state(false);
  let error = $state<string | null>(null);
  let fieldErrors = $state<Record<string, string>>({});

  const ordered = $derived([...states].sort((a, b) => compareRank(a.rank, b.rank) || a.name.localeCompare(b.name)));

  async function load() {
    loading = true;

    try {
      states = await teamsApi.states(teamId);
    } catch (failure) {
      error = failure instanceof ApiError ? failure.message : 'Could not load the workflow states.';
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

  // Someone else's change shows up without a refresh, unless it would pull a half-typed edit away.
  // Our own arrive here too, and reloading after them is harmless: the list is what the server has.
  $effect(() =>
    realtime.on('WorkflowStateChanged', (change) => {
      if (change.teamId === teamId && !editing && !busy) void load();
    })
  );

  function edit(target: WorkflowStateDto | 'new') {
    const state = target === 'new' ? null : target;

    editing = state?.id ?? 'new';
    name = state?.name ?? '';
    type = state?.type ?? 'Unstarted';
    color = state?.color ?? DEFAULT_COLOR;
    isDefault = state?.isDefault ?? false;
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
      const body = { name: name.trim(), type, color, isDefault };

      if (editing === 'new') {
        await teamsApi.createState(teamId, body);
      } else {
        // Only ever set, never cleared: a team has one default, and the way to move it is to make
        // another state the default rather than to leave the team without one.
        const current = states.find((state) => state.id === editing);
        await teamsApi.updateState(teamId, editing, {
          name: body.name,
          type,
          color,
          ...(isDefault && !current?.isDefault ? { isDefault: true } : {})
        });
      }

      // Reloaded rather than patched: making one state the default quietly clears another.
      states = await teamsApi.states(teamId);
      workspace.invalidate(teamId);
      editing = null;
    } catch (failure) {
      if (failure instanceof ApiError) {
        error = failure.message;
        fieldErrors = failure.fieldErrors;
      } else {
        error = 'Saving the state failed.';
      }
    } finally {
      busy = false;
    }
  }

  /**
   * Moves a state one place along the board.
   *
   * Only the state that moves is written: it takes a rank key between the two states it now sits
   * between, and every other column keeps the key it has.
   */
  async function move(state: WorkflowStateDto, offset: -1 | 1) {
    const from = ordered.findIndex((candidate) => candidate.id === state.id);
    const to = from + offset;
    if (busy || from < 0 || to < 0 || to >= ordered.length) return;

    // Where it lands among the others: past the one below it, or ahead of the one above it.
    const others = ordered.filter((candidate) => candidate.id !== state.id);
    const rank = rankAt(
      others.map((candidate) => candidate.rank),
      offset === 1 ? from + 1 : from - 1
    );

    const previous = states;
    states = states.map((candidate) => (candidate.id === state.id ? { ...candidate, rank } : candidate));
    busy = true;

    try {
      await teamsApi.updateState(teamId, state.id, { rank });
      workspace.invalidate(teamId);
    } catch (failure) {
      states = previous;
      toasts.error(failure instanceof ApiError ? failure.message : 'Moving the state failed.');
    } finally {
      busy = false;
      // Landed or refused, the server's order is the truth.
      void load();
    }
  }

  async function remove(state: WorkflowStateDto) {
    const answer = await confirm.ask({
      title: `Delete ${state.name}?`,
      message:
        'The column comes off the board. A state can only be deleted once no issues are in it — move them to another state first.',
      confirmLabel: 'Delete',
      cancelLabel: 'Cancel',
      danger: true
    });

    if (!answer) return;

    busy = true;

    try {
      await teamsApi.deleteState(teamId, state.id);
      states = states.filter((candidate) => candidate.id !== state.id);
      workspace.invalidate(teamId);
    } catch (failure) {
      toasts.error(failure instanceof ApiError ? failure.message : 'Deleting the state failed.');
    } finally {
      busy = false;
    }
  }

  function onKeydown(event: KeyboardEvent) {
    if (event.key === 'Enter' && !event.shiftKey && (event.target as HTMLElement).tagName === 'INPUT') {
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
        <label for="state-name">Name</label>
        <!-- svelte-ignore a11y_autofocus -->
        <input
          id="state-name"
          bind:value={name}
          class="input"
          class:invalid={Boolean(fieldErrors.name)}
          maxlength="60"
          placeholder="In Review"
          autofocus
          disabled={busy} />
        {#if fieldErrors.name}<p class="field-error">{fieldErrors.name}</p>{/if}
      </div>

      <div class="field">
        <label for="state-type">Type</label>
        <Select
          id="state-type"
          options={typeOptions}
          value={type}
          onchange={(value) => (type = value)}
          variant="field"
          disabled={busy}
          label="Type" />
        <p class="hint muted">{TYPE_HINT[type]}</p>
      </div>
    </div>

    <div class="field">
      <span class="field-label">Colour</span>
      <ColorPicker value={color} onchange={(value) => (color = value)} disabled={busy} />
      {#if fieldErrors.color}<p class="field-error">{fieldErrors.color}</p>{/if}
    </div>

    <label class="check">
      <input
        type="checkbox"
        bind:checked={isDefault}
        disabled={busy || (editing !== 'new' && states.find((state) => state.id === editing)?.isDefault)} />
      <span>New issues start here</span>
      <span class="muted">— the team's default state{editing !== 'new' && states.find((state) => state.id === editing)?.isDefault ? '. Make another state the default to move it.' : ''}</span>
    </label>

    <div class="editor-actions">
      <span class="preview">
        <span style:color={color}><Icon name={STATE_TYPE[type].icon} size={13} /></span>
        {name.trim() || 'State'}
      </span>
      <span class="spacer"></span>
      <button type="button" class="btn btn-sm" onclick={cancel} disabled={busy}>Cancel</button>
      <button type="button" class="btn btn-sm btn-primary" onclick={() => void save()} disabled={busy || !name.trim()}>
        {editing === 'new' ? 'Create state' : 'Save state'}
      </button>
    </div>
  </div>
{/snippet}

<section class="panel">
  <div class="panel-title">
    <span>Workflow states</span>
    <span class="badge">{states.length}</span>
  </div>

  <p class="muted intro">The columns of this team's board, left to right.</p>

  {#if loading && states.length === 0}
    <p class="muted">Loading…</p>
  {:else}
    <ul class="states">
      {#each ordered as state, index (state.id)}
        {#if editing === state.id}
          <li>{@render editor()}</li>
        {:else}
          <li class="row">
            <span class="glyph" style:color={state.color}>
              <Icon name={STATE_TYPE[state.type].icon} size={13} />
            </span>
            <span class="name truncate">{state.name}</span>
            {#if state.isDefault}<span class="badge" title="New issues start here">Default</span>{/if}
            <span class="type muted">{STATE_TYPE[state.type].label}</span>

            {#if canAdminister}
              <div class="actions">
                <button
                  type="button"
                  class="btn btn-quiet btn-icon btn-sm"
                  onclick={() => void move(state, -1)}
                  disabled={busy || editing !== null || index === 0}
                  aria-label="Move {state.name} left"
                  title="Move left on the board">
                  <Icon name="chevron-up" size={13} />
                </button>
                <button
                  type="button"
                  class="btn btn-quiet btn-icon btn-sm"
                  onclick={() => void move(state, 1)}
                  disabled={busy || editing !== null || index === ordered.length - 1}
                  aria-label="Move {state.name} right"
                  title="Move right on the board">
                  <Icon name="chevron-down" size={13} />
                </button>
                <button
                  type="button"
                  class="btn btn-quiet btn-icon btn-sm"
                  onclick={() => edit(state)}
                  disabled={busy || editing !== null}
                  aria-label="Edit {state.name}">
                  <Icon name="pencil" size={13} />
                </button>
                <button
                  type="button"
                  class="btn btn-quiet btn-icon btn-sm"
                  onclick={() => void remove(state)}
                  disabled={busy || editing !== null || states.length <= 1}
                  aria-label="Delete {state.name}"
                  title={states.length <= 1 ? 'A team needs at least one state' : undefined}>
                  <Icon name="x" size={13} />
                </button>
              </div>
            {/if}
          </li>
        {/if}
      {/each}
    </ul>

    {#if editing === 'new'}
      {@render editor()}
    {:else if canAdminister}
      <div class="add">
        <button type="button" class="btn btn-sm" onclick={() => edit('new')} disabled={busy || editing !== null}>
          <Icon name="plus" size={13} />
          New state
        </button>
      </div>
    {/if}
  {/if}
</section>

<style>
  .intro {
    margin-bottom: var(--s-3);
    font-size: var(--text-sm);
  }

  .states {
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

  .glyph {
    display: grid;
    flex: none;
    place-items: center;
  }

  .name {
    min-width: 0;
    font-weight: 500;
  }

  .type {
    flex: 1;
    font-size: var(--text-sm);
  }

  .actions {
    display: flex;
    flex: none;
    gap: 2px;
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

  .hint {
    margin-top: var(--s-1);
    font-size: var(--text-xs);
  }

  .check {
    display: flex;
    flex-wrap: wrap;
    align-items: center;
    gap: var(--s-2);
    font-size: var(--text-sm);
  }

  .check input {
    accent-color: var(--accent);
  }

  .editor-actions {
    display: flex;
    align-items: center;
    gap: var(--s-3);
  }

  .preview {
    display: inline-flex;
    align-items: center;
    gap: var(--s-2);
    font-size: var(--text-sm);
    font-weight: 600;
  }

  .spacer {
    flex: 1;
  }

  .add {
    padding-top: var(--s-4);
    border-top: 1px solid var(--split);
  }
</style>
