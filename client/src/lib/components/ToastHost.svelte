<script lang="ts">
  import Icon from './Icon.svelte';
  import { toasts } from './toast.svelte';
  import type { IconName } from '$lib/icons/icons';

  const ICONS: Record<string, IconName> = {
    success: 'circle-check',
    error: 'circle-alert',
    info: 'info'
  };
</script>

<div class="toasts" role="status" aria-live="polite">
  {#each toasts.items as toast (toast.id)}
    <div class="toast {toast.kind}">
      <Icon name={ICONS[toast.kind]} size={15} />
      <span class="message">{toast.message}</span>
      <button
        type="button"
        class="btn btn-quiet btn-icon btn-sm"
        onclick={() => toasts.dismiss(toast.id)}
        aria-label="Dismiss">
        <Icon name="x" size={13} />
      </button>
    </div>
  {/each}
</div>

<style>
  .toasts {
    position: fixed;
    right: var(--s-6);
    bottom: calc(var(--statusbar-h) + var(--s-5));
    z-index: var(--z-toast);
    display: flex;
    flex-direction: column;
    gap: var(--s-3);
    max-width: min(420px, calc(100vw - var(--s-9)));
    pointer-events: none;
  }

  .toast {
    display: flex;
    align-items: flex-start;
    gap: var(--s-3);
    padding: var(--s-4) var(--s-4) var(--s-4) var(--s-5);
    border: 1px solid var(--border);
    border-radius: var(--radius-md);
    background: var(--bg-raised);
    box-shadow: var(--shadow-md);
    pointer-events: auto;
    animation: slide-in 160ms var(--ease);
  }

  .message {
    flex: 1;
    min-width: 0;
    padding-top: 1px;
    line-height: var(--leading-normal);
    overflow-wrap: anywhere;
  }

  .success {
    border-color: var(--success-border);
    color: var(--success);
  }

  .error {
    border-color: var(--danger-border);
    color: var(--danger);
  }

  .info {
    border-color: var(--info-border);
    color: var(--info);
  }

  .toast .message {
    color: var(--fg);
  }

  @keyframes slide-in {
    from {
      opacity: 0;
      transform: translateY(6px);
    }
  }
</style>
