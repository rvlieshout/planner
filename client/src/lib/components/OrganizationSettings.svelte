<script lang="ts">
  import { onMount } from 'svelte';
  import { ApiError, organization } from '$lib/api';
  import { fileSize } from '$lib/format';
  import Icon from '$components/Icon.svelte';
  import { toasts } from '$components/toast.svelte';

  /**
   * What the owner decides for the whole installation. Rendered only for the owner: everyone else may
   * read these values, but has nothing here to change.
   *
   * The limit is entered in gigabytes because nobody thinks in bytes; the API keeps bytes, and zero
   * there means no limit.
   */
  const GB = 1024 ** 3;

  let savedBytes = $state<number | null>(null);
  // A number input binds a number, and null while it is empty.
  let gigabytes = $state<number | null>(null);
  let saving = $state(false);
  let error = $state<string | null>(null);

  const enteredBytes = $derived(
    typeof gigabytes === 'number' && Number.isFinite(gigabytes) && gigabytes >= 0 ? Math.round(gigabytes * GB) : null
  );
  const dirty = $derived(savedBytes !== null && enteredBytes !== null && enteredBytes !== savedBytes);

  onMount(() => {
    void load();
  });

  function show(bytes: number) {
    savedBytes = bytes;
    // Up to two decimals, without trailing zeros: 5, 2.5, 0.25.
    gigabytes = Math.round((bytes / GB) * 100) / 100;
  }

  async function load() {
    try {
      show((await organization.settings()).teamStorageBytes);
    } catch (failure) {
      error = failure instanceof ApiError ? failure.message : 'The organisation settings could not be loaded.';
    }
  }

  async function save() {
    if (saving || !dirty || enteredBytes === null) return;

    saving = true;
    error = null;

    try {
      show((await organization.update({ teamStorageBytes: enteredBytes })).teamStorageBytes);
      toasts.success('Team storage limit saved.');
    } catch (failure) {
      error = failure instanceof ApiError ? failure.message : 'Saving the storage limit failed.';
    } finally {
      saving = false;
    }
  }
</script>

<section class="panel">
  <div class="panel-title"><span>Organisation</span></div>

  {#if error}
    <div class="alert alert-error"><Icon name="circle-alert" size={15} /><span>{error}</span></div>
  {/if}

  <div class="field">
    <label for="org-team-storage">Attachment storage per team (GB)</label>
    <input
      id="org-team-storage"
      class="input"
      type="number"
      min="0"
      step="0.5"
      inputmode="decimal"
      bind:value={gigabytes}
      disabled={saving || savedBytes === null} />
    <p class="muted hint">
      How much each team may hold in uploaded files. Once a team is full, uploads to its issues are
      refused until files are removed or this is raised. 0 means no limit.
      {#if savedBytes !== null}
        Currently {savedBytes === 0 ? 'no limit' : fileSize(savedBytes)}.
      {/if}
    </p>
  </div>

  <div class="actions">
    <button
      type="button"
      class="btn btn-primary btn-sm"
      onclick={() => void save()}
      disabled={saving || !dirty}>
      Save limit
    </button>
  </div>
</section>

<style>
  .input {
    max-width: 180px;
  }

  .actions {
    display: flex;
    justify-content: flex-end;
  }

  .hint {
    font-size: var(--text-xs);
  }
</style>
