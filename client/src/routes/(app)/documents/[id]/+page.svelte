<script lang="ts">
  import { page } from '$app/state';
  import { ApiError, documents } from '$lib/api';
  import type { DocumentDto } from '$lib/api/types';
  import { Permission, session } from '$lib/auth/session.svelte';
  import { chrome } from '$lib/chrome.svelte';
  import { navigate } from '$lib/navigation.svelte';
  import { confirm } from '$components/confirm.svelte';
  import Markdown from '$components/markdown/Markdown.svelte';
  import MarkdownEditor from '$components/markdown/MarkdownEditor.svelte';

  const id = $derived(page.params.id!);
  let document = $state<DocumentDto | null>(null);
  let title = $state('');
  let content = $state('');
  let editing = $state(false);
  let busy = $state(false);
  let error = $state('');
  const canEdit = $derived(!!document && !document.archivedAt && session.can(document.teamId, Permission.Write));
  const dirty = $derived(!!document && (title !== document.title || content !== document.content));

  $effect(() => {
    const requested = id;
    let current = true;
    document = null;
    editing = false;
    error = '';
    void documents.get(requested).then((loaded) => {
      if (!current) return;
      document = loaded;
      title = loaded.title;
      content = loaded.content;
    }).catch((failure) => {
      if (current) error = failure instanceof ApiError ? failure.message : 'Could not load this document.';
    });
    return () => { current = false; };
  });

  $effect(() => {
    chrome.set({ title: document?.title ?? 'Document', subtitle: 'Project document' });
    chrome.unsavedWork = () => dirty ? 'unsaved document changes' : busy ? 'a document save in progress' : null;
    return () => chrome.clear();
  });

  async function save() {
    if (!document || !canEdit || busy || !title.trim()) return;
    const requested = id;
    const saved = { title: title.trim(), content };
    busy = true;
    error = '';
    try {
      const summary = await documents.update(requested, saved);
      if (id !== requested) return;
      document = { ...document, ...summary, content: saved.content };
      title = saved.title;
      editing = false;
    } catch (failure) {
      if (id === requested) error = failure instanceof ApiError ? failure.message : 'Could not save this document.';
    } finally { busy = false; }
  }

  async function cancel() {
    if (dirty && !(await confirm.discard('unsaved document changes'))) return;
    title = document!.title;
    content = document!.content;
    editing = false;
    error = '';
  }
</script>

<article>
  {#if error}<p class="alert alert-error" role="alert">{error}</p>{/if}
  {#if document}
    <header>
      {#if document.projectId}
        <button class="btn btn-sm" onclick={() => void navigate(`/projects/${document!.projectId}`)}>Back to project</button>
      {/if}
      {#if canEdit && !editing}<button class="btn btn-sm" onclick={() => (editing = true)}>Edit document</button>{/if}
      {#if document.archivedAt}<span class="muted">Archived document</span>{/if}
    </header>
    {#if editing}
      <label for="document-title">Title</label>
      <input id="document-title" class="input" bind:value={title} maxlength="300" disabled={busy} />
      <MarkdownEditor bind:value={content} disabled={busy} rows={20} />
      <footer>
        <button class="btn btn-primary" onclick={() => void save()} disabled={busy || !dirty || !title.trim()}>{busy ? 'Saving…' : 'Save changes'}</button>
        <button class="btn" onclick={() => void cancel()} disabled={busy}>Cancel</button>
      </footer>
    {:else}
      <h1>{document.title}</h1>
      <Markdown value={document.content} placeholder="This document is empty." />
    {/if}
  {:else if !error}<p class="muted">Loading document…</p>{/if}
</article>

<style>
  article { width: 100%; height: 100%; overflow-y: auto; max-width: 960px; margin: 0 auto; padding: var(--s-6); display: flex; flex-direction: column; gap: var(--s-4); }
  article > :global(*) { flex-shrink: 0; }
  header, footer { display: flex; align-items: center; gap: var(--s-3); }
</style>
