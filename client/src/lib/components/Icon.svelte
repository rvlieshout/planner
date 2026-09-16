<script lang="ts">
  import { icons, type IconName } from '$lib/icons/icons';

  /**
   * A Lucide glyph, drawn inline.
   *
   * Inline rather than an <img> or an icon font: it inherits `currentColor`, so an icon is coloured
   * by the text around it and follows the theme without a second asset per variant, and it costs no
   * request at all. The stroke is scaled with the size the way Lucide's own SVGs do, so a 14px icon
   * and a 20px one are the same visual weight rather than the same absolute stroke.
   */
  interface Props {
    name: IconName;
    size?: number;
    /** Lucide draws at 2 at 24px. Anything smaller wants a proportionally thinner line. */
    strokeWidth?: number;
    class?: string;
    /** Give an icon a label only when it is the whole of the control; otherwise it is decoration. */
    label?: string;
  }

  let { name, size = 16, strokeWidth, class: className = '', label }: Props = $props();

  const width = $derived(strokeWidth ?? Math.max(1.4, (2 * 20) / Math.max(size, 12)));
</script>

<svg
  class={className}
  width={size}
  height={size}
  viewBox="0 0 24 24"
  fill="none"
  stroke="currentColor"
  stroke-width={width}
  stroke-linecap="round"
  stroke-linejoin="round"
  role={label ? 'img' : 'presentation'}
  aria-label={label}
  aria-hidden={label ? undefined : 'true'}
  focusable="false">
  <!-- eslint-disable-next-line svelte/no-at-html-tags -- generated at build time from lucide-static -->
  {@html icons[name]}
</svg>

<style>
  svg {
    flex: none;
    overflow: visible;
  }
</style>
