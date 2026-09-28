<script lang="ts">
  import { ApiError, projects } from '$lib/api';
  import type { DocumentSummary, Guid } from '$lib/api/types';
  import { navigate } from '$lib/navigation.svelte';
  import { realtime } from '$lib/realtime/hub.svelte';

  let { projectId }: { projectId: Guid } = $props();
  let documents = $state<DocumentSummary[]>([]);
  let error = $state('');
  let loading = $state(true);
  let generation = 0;
  async function load() {
    const version = ++generation;
    const requested = projectId;
    loading = true;
    error = '';
    try {
      const result = await projects.documents(requested);
      if (version === generation && requested === projectId) documents = result;
    } catch (failure) {
      if (version === generation) error = failure instanceof ApiError ? failure.message : 'Could not load documents.';
    } finally { if (version === generation) loading = false; }
  }
  $effect(() => { void projectId; void load(); });
  $effect(() => realtime.on('DocumentChanged', () => void load()));
  $effect(() => realtime.onReconnected(() => void load()));
</script>

<section aria-label="Project documents">
  <h3 class="caption">Documents</h3>
  {#if error}
    <p class="field-error">{error} <button class="btn btn-sm" onclick={() => void load()}>Retry</button></p>
  {:else if loading}<p class="muted">Loading documents…</p>
  {:else}
    <div class="documents">
      {#each documents as document (document.id)}
        <button class="btn btn-sm" onclick={() => void navigate(`/documents/${document.id}`)}>{document.title}</button>
      {:else}<p class="muted">No documents attached to this project.</p>{/each}
    </div>
  {/if}
</section>

<style>
  section { display: flex; flex-direction: column; gap: var(--s-2); }
  .documents { display: flex; flex-wrap: wrap; gap: var(--s-2); }
</style>
