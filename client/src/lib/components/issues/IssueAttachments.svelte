<script lang="ts">
  import { ApiError, attachments as attachmentsApi, issues as issuesApi } from '$lib/api';
  import type { AttachmentDto, Guid } from '$lib/api/types';
  import { fileSize, relativeTime } from '$lib/format';
  import Icon from '$components/Icon.svelte';
  import { confirm } from '$components/confirm.svelte';
  import { toasts } from '$components/toast.svelte';

  /**
   * Files on an issue, either way the server supports them.
   *
   * Uploading sends the bytes to the API, which keeps them outside the web root and behind the
   * issue's own team permission. Linking records a location — a share, an object store — and stores
   * metadata only. Both end up in the same list, and only the uploaded ones can be downloaded from
   * here, because the other kind is somewhere this application has never been.
   */
  interface Props {
    issueId: Guid;
    attachments: AttachmentDto[];
    canAttach: boolean;
    onchange: (attachments: AttachmentDto[]) => void;
    /** True while a half-typed link is sitting in the form, so the page's guard can mention it. */
    ondraft?: (hasDraft: boolean) => void;
  }

  let { issueId, attachments, canAttach, onchange, ondraft }: Props = $props();

  const MAX_BYTES = 20 * 1024 * 1024;

  let linking = $state(false);
  let linkName = $state('');
  let linkUri = $state('');
  let busy = $state(false);
  let error = $state<string | null>(null);
  let fileInput = $state<HTMLInputElement | null>(null);

  $effect(() => {
    ondraft?.(linking && (linkName.trim().length > 0 || linkUri.trim().length > 0));
  });

  async function upload(event: Event) {
    const input = event.currentTarget as HTMLInputElement;
    const file = input.files?.[0];
    if (!file) return;

    // Checked here as well as on the server, so a 20 MB upload is refused before it is sent rather
    // than after it has crossed the network.
    if (file.size > MAX_BYTES) {
      error = 'Attachments must be 20 MB or smaller.';
      input.value = '';
      return;
    }

    busy = true;
    error = null;

    try {
      onchange([...attachments, await issuesApi.uploadFile(issueId, file)]);
    } catch (failure) {
      error = failure instanceof ApiError ? failure.message : 'That upload failed.';
    } finally {
      busy = false;
      input.value = '';
    }
  }

  async function link() {
    const fileName = linkName.trim() || linkUri.trim().split(/[/\\]/).pop() || 'Link';
    const storageUri = linkUri.trim();

    if (!storageUri) return;

    busy = true;
    error = null;

    try {
      onchange([...attachments, await issuesApi.linkAttachment(issueId, { fileName, storageUri })]);
      linkName = '';
      linkUri = '';
      linking = false;
    } catch (failure) {
      error = failure instanceof ApiError ? failure.message : 'Linking that file failed.';
    } finally {
      busy = false;
    }
  }

  async function download(attachment: AttachmentDto) {
    if (!attachmentsApi.isStored(attachment.storageUri)) {
      window.open(attachment.storageUri, '_blank', 'noopener');
      return;
    }

    try {
      const blob = await attachmentsApi.download(attachment.id);
      const url = URL.createObjectURL(blob);

      // A synthetic anchor is the only way a browser saves a blob under a chosen name.
      const anchor = document.createElement('a');
      anchor.href = url;
      anchor.download = attachment.fileName;
      anchor.click();

      URL.revokeObjectURL(url);
    } catch (failure) {
      toasts.error(failure instanceof ApiError ? failure.message : 'That download failed.');
    }
  }

  async function remove(attachment: AttachmentDto) {
    const answer = await confirm.ask({
      title: `Remove ${attachment.fileName}?`,
      message: 'It leaves this issue. Bytes already stored on the server are not purged.',
      confirmLabel: 'Remove',
      cancelLabel: 'Cancel',
      danger: true
    });

    if (!answer) return;

    try {
      await attachmentsApi.remove(attachment.id);
      onchange(attachments.filter((candidate) => candidate.id !== attachment.id));
    } catch (failure) {
      toasts.error(failure instanceof ApiError ? failure.message : 'Removing that file failed.');
    }
  }
</script>

<section class="attachments">
  <header class="section-header">
    <h3 class="caption">Attachments</h3>
    {#if canAttach}
      <div class="row-tight">
        <button
          type="button"
          class="btn btn-quiet btn-icon btn-sm"
          onclick={() => fileInput?.click()}
          disabled={busy}
          title="Upload a file"
          aria-label="Upload a file">
          <Icon name="upload" size={13} />
        </button>
        <button
          type="button"
          class="btn btn-quiet btn-icon btn-sm"
          onclick={() => (linking = !linking)}
          disabled={busy}
          title="Link a file held elsewhere"
          aria-label="Link a file held elsewhere">
          <Icon name="link" size={13} />
        </button>
      </div>
    {/if}
  </header>

  <input
    bind:this={fileInput}
    type="file"
    class="visually-hidden"
    onchange={(event) => void upload(event)} />

  {#if error}
    <p class="field-error">{error}</p>
  {/if}

  {#if linking}
    <div class="link-form">
      <input bind:value={linkUri} class="input" placeholder="https://… or \\server\share\file" />
      <input bind:value={linkName} class="input" placeholder="Display name (optional)" />
      <div class="row-tight">
        <button
          type="button"
          class="btn btn-primary btn-sm"
          onclick={() => void link()}
          disabled={busy || !linkUri.trim()}>
          Add link
        </button>
        <button type="button" class="btn btn-sm" onclick={() => (linking = false)}>Cancel</button>
      </div>
    </div>
  {/if}

  <ul class="list">
    {#each attachments as attachment (attachment.id)}
      <li>
        <button type="button" class="file" onclick={() => void download(attachment)}>
          <Icon name={attachmentsApi.isStored(attachment.storageUri) ? 'file-text' : 'external-link'} size={13} />
          <span class="truncate">{attachment.fileName}</span>
          {#if attachment.sizeBytes}<span class="muted size">{fileSize(attachment.sizeBytes)}</span>{/if}
        </button>

        <span class="muted when">{relativeTime(attachment.createdAt)}</span>

        {#if canAttach}
          <button
            type="button"
            class="btn btn-quiet btn-icon btn-sm"
            onclick={() => void remove(attachment)}
            aria-label="Remove {attachment.fileName}">
            <Icon name="x" size={12} />
          </button>
        {/if}
      </li>
    {:else}
      <li class="none muted">No files.</li>
    {/each}
  </ul>
</section>

<style>
  .attachments {
    display: flex;
    flex-direction: column;
    gap: var(--s-3);
  }

  .list {
    display: flex;
    flex-direction: column;
    gap: var(--s-1);
  }

  li {
    display: flex;
    align-items: center;
    gap: var(--s-2);
  }

  .file {
    display: flex;
    flex: 1;
    align-items: center;
    gap: var(--s-2);
    min-width: 0;
    height: var(--row-h-sm);
    padding: 0 var(--s-2);
    border: 0;
    border-radius: var(--radius-xs);
    background: none;
    color: var(--fg);
    font-size: var(--text-sm);
    text-align: left;
    cursor: pointer;
  }

  .file:hover {
    background: var(--bg-hover);
  }

  .size,
  .when {
    flex: none;
    font-size: var(--text-xs);
  }

  .none {
    padding: 0 var(--s-2);
    font-size: var(--text-sm);
  }

  .link-form {
    display: flex;
    flex-direction: column;
    gap: var(--s-2);
    padding: var(--s-3);
    border: 1px solid var(--border);
    border-radius: var(--radius-sm);
    background: var(--bg-sunken);
  }
</style>
