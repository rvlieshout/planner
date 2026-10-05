<script lang="ts">
  import type { Snippet } from 'svelte';
  import { Popover } from 'bits-ui';

  /**
   * A panel anchored to the control that opened it: the team switcher, a property pill, a row's
   * context menu.
   *
   * Placement, flipping above the trigger when there is more room there, and closing on Escape or a
   * click elsewhere are bits-ui's. It is positioned `fixed`, so a scrolling column or an
   * `overflow: hidden` ancestor cannot clip it, and it is deliberately not portalled: inside a modal
   * <dialog> a panel moved to <body> would sit under the top layer, inert.
   *
   * The caller's trigger keeps its own click handler and its own focus — a picker walks its list with
   * the arrow keys from the trigger — so nothing here toggles, traps or moves focus.
   */
  interface Props {
    open: boolean;
    onclose: () => void;
    /** Which edge of the trigger the panel lines up with. */
    align?: 'start' | 'end';
    /** Fixed width, or `trigger` to match the control it belongs to. */
    width?: number | 'trigger';
    trigger: Snippet<[{ open: boolean }]>;
    children: Snippet;
  }

  let { open, onclose, align = 'start', width, trigger, children }: Props = $props();

  let anchor = $state<HTMLElement | null>(null);

  // The wrapper is `display: contents`, so the control keeps whatever layout its parent gives it —
  // and so the wrapper has no box to hang a panel off. The control inside it is what gets measured.
  const control = $derived(open ? ((anchor?.firstElementChild ?? anchor) as HTMLElement | null) : null);

  const minWidth = $derived(
    width === undefined ? undefined : width === 'trigger' ? 'var(--bits-popover-anchor-width)' : `${width}px`
  );

  const leaveFocus = (event: Event) => event.preventDefault();
</script>

<span class="anchor" bind:this={anchor}>
  {@render trigger({ open })}
</span>

<Popover.Root
  bind:open={
    () => open,
    (next) => {
      if (!next) onclose();
    }
  }>
  <Popover.Content
    class="popover"
    customAnchor={control}
    {align}
    sideOffset={4}
    collisionPadding={8}
    strategy="fixed"
    trapFocus={false}
    onOpenAutoFocus={leaveFocus}
    onCloseAutoFocus={leaveFocus}
    style={minWidth && `min-width: ${minWidth}`}>
    {@render children()}
  </Popover.Content>
</Popover.Root>

<style>
  .anchor {
    display: contents;
  }

  :global(.popover) {
    z-index: var(--z-dropdown);
    display: flex;
    flex-direction: column;
    overflow: hidden auto;
    max-height: max(160px, var(--bits-popover-content-available-height));
    padding: var(--s-2);
    border: 1px solid var(--border);
    border-radius: var(--radius-md);
    background: var(--bg-raised);
    box-shadow: var(--shadow-md);
    animation: popover-appear 90ms var(--ease);
  }

  @keyframes -global-popover-appear {
    from {
      opacity: 0;
      transform: translateY(-2px);
    }
  }
</style>
