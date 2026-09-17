<script lang="ts">
  import Icon from '$components/Icon.svelte';
  import Markdown from './Markdown.svelte';
  import MarkdownEditor from './MarkdownEditor.svelte';
  import type { Guid } from '$lib/api/types';

  /**
   * A description: read as a document, written as markdown.
   *
   * It opens rendered, because a description is read far more often than it is written, and the
   * document is the point of writing markdown at all. Getting into the editor is a click on the text,
   * the Edit button, or whatever shortcut the page binds to `edit()` — three ways in, because the one
   * thing worse than no edit affordance is one nobody finds.
   *
   * Nothing is saved here. The value is bound, so an edit lands in whatever the page already tracks
   * as unsaved work and is written by the page's own Save. Leaving the editor is not a commitment,
   * and `Escape` goes back to reading without dropping what was typed.
   */
  interface Props {
    value: string;
    canEdit?: boolean;
    uploading?: boolean;
    /** The issue pasted images are uploaded to. Omitted where there is no issue to attach to. */
    issueId?: Guid | null;
    /** Shown in place of an empty document, and inside the empty editor. */
    placeholder?: string;
    rows?: number;
    label?: string;
  }

  let {
    value = $bindable(),
    canEdit = true, uploading = $bindable(false),
    issueId = null,
    placeholder = 'Add a description…',
    rows = 10,
    label = 'Description'
  }: Props = $props();

  let editing = $state(false);

  export function edit(): void {
    if (canEdit) editing = true;
  }

  export function isEditing(): boolean {
    return editing;
  }

  function onKeyDown(event: KeyboardEvent) {
    // Escape leaves the editor rather than the page: the dialog this often sits inside would close
    // otherwise, taking the edit with it.
    if (event.key === 'Escape') {
      event.stopPropagation();
      event.preventDefault();
      if (!uploading) editing = false;
    }
  }
</script>

<section class="editable" aria-label={label}>
  {#if editing}
    <MarkdownEditor bind:value bind:uploading focusOnMount {issueId} {placeholder} {rows} onkeydown={onKeyDown} />

    <div class="actions">
      <button type="button" class="btn btn-sm btn-quiet" disabled={uploading} onclick={() => (editing = false)}>
        <Icon name="check" size={13} />
        Done
      </button>
      <span class="muted hint">Escape closes the editor · saved with the page</span>
    </div>
  {:else}
    <div class="reading">
      {#if canEdit}
        <button
          type="button"
          class="btn btn-sm btn-quiet edit"
          onclick={() => (editing = true)}
          title="Edit {label.toLowerCase()} (E)">
          <Icon name="pencil" size={13} />
          Edit
        </button>
      {/if}

      <!--
        The document doubles as the way in, so the whole block is clickable where it may be edited.
        A real button around a rendered document would swallow the links and the text selection
        inside it, so this is a plain element with a click and a keyboard equivalent on it instead.
      -->
      <!-- svelte-ignore a11y_no_static_element_interactions -->
      <!-- svelte-ignore a11y_click_events_have_key_events -->
      <div
        class="document"
        class:clickable={canEdit}
        onclick={(event) => {
          // A click that landed on a link or on a selection is that, not a request to edit.
          if (!canEdit) return;
          if ((event.target as HTMLElement).closest('a')) return;
          if (!window.getSelection()?.isCollapsed) return;

          editing = true;
        }}>
        <Markdown
          {value}
          placeholder={canEdit ? placeholder : `No ${label.toLowerCase()}.`} />
      </div>
    </div>
  {/if}
</section>

<style>
  .editable {
    min-width: 0;
  }

  .reading {
    position: relative;
  }

  .document {
    min-height: 32px;
    padding: var(--s-3) var(--s-4);
    border: 1px solid transparent;
    border-radius: var(--radius-sm);
  }

  .document.clickable {
    cursor: text;
  }

  .document.clickable:hover {
    border-color: var(--border);
    background: var(--bg-sunken);
  }

  /* Out of the way until the block is hovered or the button itself is focused, so a document being
     read is a document rather than a toolbar. */
  .edit {
    position: absolute;
    top: var(--s-2);
    right: var(--s-2);
    z-index: 1;
    opacity: 0;
    transition: opacity var(--duration) var(--ease);
  }

  .reading:hover .edit,
  .edit:focus-visible {
    opacity: 1;
  }

  .actions {
    display: flex;
    align-items: center;
    gap: var(--s-4);
    margin-top: var(--s-3);
  }

  .hint {
    font-size: var(--text-xs);
  }
</style>

