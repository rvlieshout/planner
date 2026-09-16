<script lang="ts">
  import type { Snippet } from 'svelte';

  /**
   * A panel anchored to the control that opened it: the team switcher, a property pill, a row's
   * context menu.
   *
   * Positioned `fixed` against the trigger's own rectangle rather than absolutely inside it, so a
   * scrolling column or an `overflow: hidden` ancestor cannot clip it, and flipped above the trigger
   * when there is more room there — a picker that opens off the bottom of the window is a picker with
   * one option visible.
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
  let panel = $state<HTMLElement | null>(null);
  let position = $state({ top: 0, left: 0, width: 0, maxHeight: 320 });

  const GAP = 4;
  const MARGIN = 8;

  /**
   * The rectangle to hang the panel off.
   *
   * The wrapper around the trigger is `display: contents` so that it adds no box of its own and the
   * control keeps whatever layout its parent gives it — which is the point, since these sit inside
   * flex rows and grid cells. The consequence is that the wrapper has no box to measure either:
   * `getBoundingClientRect()` on it returns all zeros, and a panel placed from that lands in the
   * top-left corner of the window. So the control inside it is what gets measured.
   */
  function anchorRect(): DOMRect | null {
    const element = anchor?.firstElementChild ?? anchor;
    return element?.getBoundingClientRect() ?? null;
  }

  function place() {
    const rect = anchorRect();
    if (!rect) return;
    const panelWidth = width === 'trigger' ? rect.width : (width ?? panel?.offsetWidth ?? rect.width);
    const below = window.innerHeight - rect.bottom - GAP - MARGIN;
    const above = rect.top - GAP - MARGIN;
    const flip = below < 180 && above > below;
    const height = panel?.offsetHeight ?? 0;

    position = {
      top: flip ? Math.max(MARGIN, rect.top - GAP - height) : rect.bottom + GAP,
      left: clamp(
        align === 'end' ? rect.right - panelWidth : rect.left,
        MARGIN,
        window.innerWidth - panelWidth - MARGIN
      ),
      width: panelWidth,
      maxHeight: Math.max(160, flip ? above : below)
    };
  }

  const clamp = (value: number, min: number, max: number) => Math.min(Math.max(value, min), Math.max(min, max));

  $effect(() => {
    if (!open) return;

    place();

    // Re-placed on scroll and resize, because `fixed` does not follow the trigger on its own. Capture
    // phase, so a scrolling container inside the page is heard as well as the window.
    const reposition = () => place();
    window.addEventListener('scroll', reposition, true);
    window.addEventListener('resize', reposition);

    return () => {
      window.removeEventListener('scroll', reposition, true);
      window.removeEventListener('resize', reposition);
    };
  });

  function onPointerDown(event: PointerEvent) {
    if (!open) return;

    const target = event.target as Node;
    if (anchor?.contains(target) || panel?.contains(target)) return;

    onclose();
  }

  function onKeyDown(event: KeyboardEvent) {
    if (open && event.key === 'Escape') {
      event.stopPropagation();
      onclose();
    }
  }
</script>

<svelte:window onpointerdown={onPointerDown} onkeydown={onKeyDown} />

<span class="anchor" bind:this={anchor}>
  {@render trigger({ open })}
</span>

{#if open}
  <div
    bind:this={panel}
    class="popover"
    style:top="{position.top}px"
    style:left="{position.left}px"
    style:min-width="{position.width}px"
    style:max-height="{position.maxHeight}px">
    {@render children()}
  </div>
{/if}

<style>
  .anchor {
    display: contents;
  }

  .popover {
    position: fixed;
    z-index: var(--z-dropdown);
    display: flex;
    flex-direction: column;
    overflow: hidden auto;
    padding: var(--s-2);
    border: 1px solid var(--border);
    border-radius: var(--radius-md);
    background: var(--bg-raised);
    box-shadow: var(--shadow-md);
    animation: appear 90ms var(--ease);
  }

  @keyframes appear {
    from {
      opacity: 0;
      transform: translateY(-2px);
    }
  }
</style>
