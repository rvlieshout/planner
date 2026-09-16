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
      onclick={() => confirm.answer(true)}>
      {pending?.confirmLabel ?? 'Discard'}
    </button>
  {/snippet}
</Modal>

<style>
  .message {
    color: var(--fg-secondary);
    line-height: var(--leading-relaxed);
  }
</style>
