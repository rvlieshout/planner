<script lang="ts">
  import { onMount } from 'svelte';
  import { MarkdownEditor as CartaEditor } from 'carta-md';
  import { createWriter } from '$lib/markdown/carta';
  import {
    ATTACHMENT_SCHEME, IMAGE_SIZES, announceUpload, resizeReference, resolveAttachments, type ImageSize
  } from '$lib/markdown/attachments';
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

  /*
   * Resizing an image from the preview: a click on it puts a size bar over its corner, and a size
   * rewrites that one reference's `#size=` in the source, through the textarea so it is undone like
   * typing. The bar remembers which image by id and occurrence, not by element, because every edit
   * re-renders the preview and replaces the elements under it.
   */
  interface Sizing { id: string; occurrence: number; size: ImageSize | null; top: number; left: number }
  let sizing = $state<Sizing | null>(null);
  const sizeLabels: Record<ImageSize, string> = { s: 'S', m: 'M', l: 'L', full: 'Full' };
  const sizeTitles: Record<ImageSize, string> = {
    s: 'Small, 240px wide', m: 'Medium, 480px wide', l: 'Large, 720px wide', full: 'The full width of the text'
  };

  function pickImage(event: MouseEvent) {
    const target = event.target as HTMLElement;
    if (target.closest('.size-bar')) return;

    const image = target.closest<HTMLImageElement>('.carta-renderer img[data-attachment]');
    if (!image || disabled || uploading) {
      sizing = null;
      return;
    }

    const id = image.dataset.attachment!;
    const same = [...container.querySelectorAll<HTMLImageElement>('.carta-renderer img[data-attachment]')]
      .filter((candidate) => candidate.dataset.attachment === id);
    const box = container.getBoundingClientRect();
    const rect = image.getBoundingClientRect();

    sizing = {
      id,
      occurrence: same.indexOf(image),
      size: (image.dataset.size as ImageSize | undefined) ?? null,
      top: rect.top - box.top + 6,
      left: rect.left - box.left + 6
    };
  }

  function resize(size: ImageSize | null) {
    const input = carta.input;
    if (!sizing || !input) return;

    const textarea = input.textarea;
    const change = resizeReference(textarea.value, sizing.id, sizing.occurrence, size);
    if (!change) {
      sizing = null;
      return;
    }

    textarea.setRangeText(change.text, change.start, change.end, 'preserve');
    input.update();
    input.history.saveState(textarea.value, textarea.selectionStart);
    sizing = { ...sizing, size };
  }

  function keydown(event: KeyboardEvent) {
    // Escape takes the size bar away first, before it reaches whatever closes the editor itself.
    if (sizing && event.key === 'Escape') {
      event.stopPropagation();
      event.preventDefault();
      sizing = null;
      return;
    }

    onkeydown?.(event);
  }

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
          // Announced even if the markdown is not inserted below: the file is on the issue either way.
          announceUpload(targetIssue, attachment);
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
  onkeydown={keydown} onpaste={receive} ondrop={receive} onclick={pickImage}
  onscrollcapture={() => (sizing = null)} onfocusin={(event) => {
    if ((event.target as HTMLElement).tagName === 'TEXTAREA') sizing = null;
  }}
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
  {#if sizing}
    <div class="size-bar" role="toolbar" aria-label="Image size" style:top="{sizing.top}px" style:left="{sizing.left}px">
      <Icon name="image" size={13} />
      <button type="button" class:active={sizing.size === null} aria-pressed={sizing.size === null}
        title="Fit the text, capped in height" onclick={() => resize(null)}>Auto</button>
      {#each IMAGE_SIZES as size (size)}
        <button type="button" class:active={sizing.size === size} aria-pressed={sizing.size === size}
          title={sizeTitles[size]} onclick={() => resize(size)}>{sizeLabels[size]}</button>
      {/each}
    </div>
  {/if}
  <div class="footer">
    {#if issueId}
      <input bind:this={picker} type="file" accept={imageTypes.join(',')} multiple hidden
        onchange={() => { void upload(Array.from(picker?.files ?? [])); if (picker) picker.value = ''; }} />
      <button type="button" class="btn btn-sm btn-quiet" disabled={disabled || uploading}
        onclick={() => picker?.click()}><Icon name="paperclip" size={14} />Add images</button>
      <span class="muted">or paste or drop images here · click one in Preview to resize it</span>
    {:else}
      <span class="muted">Markdown supported · {uploadHint}</span>
    {/if}
    <span role="status">{status}</span>
  </div>
  {#if failure}<p class="upload-error" role="alert">{failure}</p>{/if}
</div>

<style>
  .editor { position: relative; min-width: 0; }
  .size-bar {
    position: absolute; z-index: 2; display: flex; align-items: center; gap: 2px;
    padding: 2px 2px 2px var(--s-2); border: 1px solid var(--border); border-radius: var(--radius-sm);
    background: var(--bg-surface); box-shadow: var(--shadow-md); color: var(--fg-tertiary);
  }
  .size-bar button {
    min-width: 26px; height: 22px; padding: 0 var(--s-2); border: 0; border-radius: var(--radius-xs);
    background: none; color: var(--fg-secondary); font: inherit; font-size: var(--text-xs); cursor: pointer;
  }
  .size-bar button:hover { background: var(--bg-hover); color: var(--fg); }
  .size-bar button.active { background: var(--accent-subtle); color: var(--accent); font-weight: 600; }
  .dragging { outline: 2px dashed var(--accent); outline-offset: 3px; border-radius: var(--radius-sm); }
  .footer { display: flex; flex-wrap: wrap; align-items: center; gap: var(--s-3); padding-top: var(--s-2); font-size: var(--text-xs); }
  .upload-error { margin-top: var(--s-2); color: var(--danger, var(--fg)); font-size: var(--text-sm); }
</style>


