<script lang="ts">
  import { onMount } from 'svelte';
  import { MarkdownEditor as CartaEditor } from 'carta-md';
  import { createWriter } from '$lib/markdown/carta';
  import { ATTACHMENT_SCHEME, resolveAttachments } from '$lib/markdown/attachments';
  import { issues as issuesApi, ApiError } from '$lib/api';
  import { toasts } from '$components/toast.svelte';
  import Icon from '$components/Icon.svelte';
  import type { Guid } from '$lib/api/types';

  interface Props {
    value: string;
    issueId?: Guid | null;
    placeholder?: string;
    disabled?: boolean;
    rows?: number;
    uploadHint?: string;
    focusOnMount?: boolean;
    uploading?: boolean;
    onkeydown?: (event: KeyboardEvent) => void;
  }

  let {
    value = $bindable(), issueId = null, placeholder = 'Write in markdown…',
    disabled = false, rows = 8, uploadHint = 'Files can only be attached to an issue.',
    focusOnMount = false, uploading = $bindable(false), onkeydown
  }: Props = $props();

  const carta = createWriter();
  let container: HTMLDivElement;
  let picker = $state<HTMLInputElement>();
  let dragging = $state(false);
  let status = $state('');
  let failure = $state('');
  let mounted = false;
  const imageTypes = ['image/png', 'image/jpeg', 'image/gif', 'image/webp', 'image/avif', 'image/svg+xml'];

  onMount(() => {
    mounted = true;
    if (focusOnMount) carta.input?.textarea.focus();
    const preview = container.querySelector<HTMLElement>('.carta-renderer');
    const observer = new MutationObserver(() => preview && resolveAttachments(preview));
    if (preview) observer.observe(preview, { childList: true, subtree: true });
    return () => { mounted = false; observer.disconnect(); };
  });

  async function upload(files: File[]) {
    if (disabled || uploading || !files.length) return;
    if (!issueId) { toasts.info(uploadHint); return; }
    const input = carta.input;
    if (!input) return;
    const images = files.filter((file) => imageTypes.includes(file.type));
    failure = images.length !== files.length ? 'Choose PNG, JPEG, GIF, WebP, AVIF or SVG images.' : '';
    if (!images.length) return;

    const targetIssue = issueId;
    const textarea = input.textarea;
    const start = textarea.selectionStart;
    const end = textarea.selectionEnd;
    const original = textarea.value;
    uploading = true;
    const markdown: string[] = [];
    try {
      for (const [index, file] of images.entries()) {
        status = `Uploading image ${index + 1} of ${images.length}…`;
        try {
          const attachment = await issuesApi.uploadFile(targetIssue, file);
          const label = file.name.replace(/[\\\[\]]/g, '\\$&').replace(/[\r\n]/g, ' ');
          markdown.push(`![${label}](${ATTACHMENT_SCHEME}${attachment.id})`);
        } catch (error) {
          failure = error instanceof ApiError ? error.message : `Could not upload ${file.name}. Please try again.`;
        }
      }
      // Never insert into another issue or a draft cleared during the request.
      if (!mounted || issueId !== targetIssue || textarea.value !== original || !markdown.length) return;
      const inserted = markdown.join('\n\n');
      textarea.setRangeText(inserted, start, end, 'end');
      input.update();
      input.history.saveState(textarea.value, textarea.selectionStart);
    } finally {
      uploading = false;
      status = '';
      if (mounted && textarea.getClientRects().length > 0) requestAnimationFrame(() => textarea.focus());
    }
  }

  function receive(event: ClipboardEvent | DragEvent) {
    const transfer = 'clipboardData' in event ? event.clipboardData : event.dataTransfer;
    const files = Array.from(transfer?.files ?? []);
    if (!files.length) return;
    event.preventDefault();
    dragging = false;
    void upload(files);
  }
</script>

<!-- svelte-ignore a11y_no_static_element_interactions -->
<div bind:this={container} class="editor" class:dragging style:--carta-rows={rows}
  {onkeydown} onpaste={receive} ondrop={receive}
  ondragover={(event) => {
    if (!event.dataTransfer?.types.includes('Files')) return;
    event.preventDefault();
    dragging = !disabled && !uploading;
  }}
  ondragleave={(event) => {
    if (!container.contains(event.relatedTarget as Node)) dragging = false;
  }}>
  <div inert={disabled || uploading}>
    <CartaEditor {carta} bind:value {placeholder} mode="tabs" theme="planner"
      textarea={{ disabled: disabled || uploading, 'aria-label': placeholder }} />
  </div>
  <div class="footer">
    {#if issueId}
      <input bind:this={picker} type="file" accept={imageTypes.join(',')} multiple hidden
        onchange={() => { void upload(Array.from(picker?.files ?? [])); if (picker) picker.value = ''; }} />
      <button type="button" class="btn btn-sm btn-quiet" disabled={disabled || uploading}
        onclick={() => picker?.click()}><Icon name="paperclip" size={14} />Add images</button>
      <span class="muted">or paste or drop images here</span>
    {:else}
      <span class="muted">Markdown supported · {uploadHint}</span>
    {/if}
    <span role="status">{status}</span>
  </div>
  {#if failure}<p class="upload-error" role="alert">{failure}</p>{/if}
</div>

<style>
  .editor { min-width: 0; }
  .dragging { outline: 2px dashed var(--accent); outline-offset: 3px; border-radius: var(--radius-sm); }
  .footer { display: flex; flex-wrap: wrap; align-items: center; gap: var(--s-3); padding-top: var(--s-2); font-size: var(--text-xs); }
  .upload-error { margin-top: var(--s-2); color: var(--danger, var(--fg)); font-size: var(--text-sm); }
</style>


