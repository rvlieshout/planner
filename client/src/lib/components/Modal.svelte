<script lang="ts">
  import type { Snippet } from 'svelte';
  import Icon from './Icon.svelte';

  /**
   * A real modal, built on <dialog>.
   *
   * The native element is what gives this the behaviour a desktop dialog has and a div cannot fake:
   * the top layer, so nothing can paint over it; a focus trap; inert content behind it; and Escape,
   * which arrives as `cancel` rather than as a key this component has to listen for globally.
   *
   * Closing is always routed through `onclose` — the title-bar button, the backdrop, Escape and the
   * form's own Cancel all end in the same place, or the page that opened the dialog would still
   * believe one was open and refuse to open the next.
   */
  interface Props {
    open: boolean;
    title: string;
    subtitle?: string;
    /** `s` 420px, `m` 640px, `l` 880px. A dialog is sized by its content, not by the viewport. */
    size?: 's' | 'm' | 'l';
    /** Off while a save is in flight: dismissing a dialog mid-request loses the user's answer. */
    dismissible?: boolean;
    onclose: () => void;
    children: Snippet;
    footer?: Snippet;
    /** The <dialog> itself, for whoever needs to tell a key pressed inside it from one pressed elsewhere. */
    dialog?: HTMLDialogElement | null;
  }

  let {
    open,
    title,
    subtitle,
    size = 'm',
    dismissible = true,
    onclose,
    children,
    footer,
    dialog = $bindable(null)
  }: Props = $props();

  $effect(() => {
    if (!dialog) return;

    if (open && !dialog.open) {
      dialog.showModal();
    } else if (!open && dialog.open) {
      dialog.close();
    }
  });

  function requestClose(event?: Event) {
    event?.preventDefault();
    if (dismissible) onclose();
  }

  /** A click that both started and ended on the backdrop — not a drag that slipped off a field. */
  function onBackdrop(event: MouseEvent) {
    if (event.target === dialog) requestClose();
  }
</script>

<dialog
  bind:this={dialog}
  class="modal size-{size}"
  oncancel={requestClose}
  onclick={onBackdrop}
  aria-labelledby="modal-title">
  <header>
    <div class="heading">
      <h2 id="modal-title">{title}</h2>
      {#if subtitle}<p class="subtitle">{subtitle}</p>{/if}
    </div>
    <button
      type="button"
      class="btn btn-quiet btn-icon btn-sm"
      onclick={() => requestClose()}
      disabled={!dismissible}
      aria-label="Close">
      <Icon name="x" size={14} />
    </button>
  </header>

  <div class="body">
    {@render children()}
  </div>

  {#if footer}
    <footer>
      {@render footer()}
    </footer>
  {/if}
</dialog>

<style>
  .modal {
    display: flex;
    flex-direction: column;
    width: min(92vw, var(--modal-w, 640px));
    max-height: min(88vh, 760px);
    padding: 0;
    border: 1px solid var(--border);
    border-radius: var(--radius-lg);
    background: var(--bg-surface);
    color: var(--fg);
    box-shadow: var(--shadow-lg);
  }

  .modal:not([open]) {
    display: none;
  }

  .size-s {
    --modal-w: 420px;
  }

  .size-m {
    --modal-w: 640px;
  }

  .size-l {
    --modal-w: 880px;
  }

  .modal::backdrop {
    background: rgb(10 11 14 / 45%);
    backdrop-filter: blur(1px);
  }

  header {
    display: flex;
    align-items: flex-start;
    gap: var(--s-4);
    padding: var(--s-5) var(--s-6);
    border-bottom: 1px solid var(--border);
  }

  .heading {
    flex: 1;
    min-width: 0;
  }

  h2 {
    font-size: var(--text-md);
    font-weight: 600;
  }

  .subtitle {
    margin-top: 2px;
    color: var(--fg-tertiary);
    font-size: var(--text-sm);
  }

  .body {
    flex: 1;
    min-height: 0;
    padding: var(--s-6);
    overflow-y: auto;
  }

  footer {
    display: flex;
    align-items: center;
    gap: var(--s-4);
    padding: var(--s-5) var(--s-6);
    border-top: 1px solid var(--border);
    background: var(--bg-sunken);
    border-radius: 0 0 var(--radius-lg) var(--radius-lg);
  }
</style>
