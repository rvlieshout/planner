<script lang="ts">
  import Modal from './Modal.svelte';
  import { confirm } from './confirm.svelte';

  /** Renders whatever `confirm.ask()` is waiting on. Mounted once, in the root layout. */
  const pending = $derived(confirm.current);

  let cancelButton = $state<HTMLButtonElement | null>(null);

  // The safe answer takes the focus, so Enter on an unread dialog keeps the work rather than losing
  // it. Escape and the backdrop already resolve the same way through Modal's close.
  $effect(() => {
    if (pending) cancelButton?.focus();
  });
</script>

<Modal
  open={pending !== null}
  title={pending?.title ?? ''}
  size="s"
  onclose={() => confirm.answer(false)}>
  <p class="message">{pending?.message ?? ''}</p>
  {#if pending?.requiredText !== undefined}
    <div class="field confirmation">
      <label for="confirmation-text">Type <strong>{pending.requiredText}</strong> to confirm.</label>
      <input
        id="confirmation-text"
        class="input"
        bind:value={confirm.confirmationText}
        autocomplete="off"
        spellcheck={false}
        aria-describedby="confirmation-hint" />
      <p id="confirmation-hint" class="message">The name must match exactly, including capital letters.</p>
    </div>
  {/if}

  {#snippet footer()}
    <span class="spacer"></span>
    <button
      bind:this={cancelButton}
      type="button"
      class="btn"
      onclick={() => confirm.answer(false)}>
      {pending?.cancelLabel ?? 'Keep editing'}
    </button>
    <button
      type="button"
      class="btn {pending?.danger ? 'btn-danger' : 'btn-primary'}"
      disabled={!confirm.canConfirm}
      onclick={() => confirm.answer(true)}>
      {pending?.confirmLabel ?? 'Discard'}
    </button>
  {/snippet}
</Modal>

<style>
  .confirmation {
    margin-top: var(--s-5);
    overflow-wrap: anywhere;
  }

  .message {
    color: var(--fg-secondary);
    line-height: var(--leading-relaxed);
  }
</style>
