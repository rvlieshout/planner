<script lang="ts">
  import { avatarHue, initials } from '$lib/format';

  /**
   * A monogram, not a Gravatar. This application makes no outbound calls, and `avatarUrl` is the
   * on-prem hook for a real picture when someone wants one.
   *
   * The font is half the diameter at every size. Two capitals in this typeface are at most 1.87× the
   * font size wide, so half the diameter keeps even "WW" inside the circle without the scale-to-fit
   * transform that leaves shorter monograms off centre.
   */
  interface Props {
    name: string | null | undefined;
    src?: string | null;
    size?: number;
    /** A different seed for the colour — an email, say, so a rename keeps the same colour. */
    seed?: string;
    title?: string;
  }

  let { name, src = null, size = 20, seed, title }: Props = $props();

  const text = $derived(initials(name));
  const hue = $derived(avatarHue(seed ?? name ?? '?'));
</script>

{#if src}
  <img
    class="avatar"
    style:width="{size}px"
    style:height="{size}px"
    {src}
    alt={name ?? ''}
    title={title ?? name ?? undefined} />
{:else}
  <span
    class="avatar monogram"
    style:width="{size}px"
    style:height="{size}px"
    style:font-size="{Math.round(size / 2)}px"
    style:--hue={hue}
    title={title ?? name ?? undefined}
    aria-hidden="true">
    {text}
  </span>
{/if}

<style>
  .avatar {
    flex: none;
    border-radius: 50%;
    object-fit: cover;
  }

  .monogram {
    display: inline-flex;
    align-items: center;
    justify-content: center;
    background: hsl(var(--hue) 45% 88%);
    color: hsl(var(--hue) 55% 28%);
    font-weight: 600;
    line-height: 1;
    user-select: none;
  }

  :global(:root[data-theme='dark']) .monogram {
    background: hsl(var(--hue) 30% 26%);
    color: hsl(var(--hue) 60% 82%);
  }

  @media (prefers-color-scheme: dark) {
    :global(:root:not([data-theme='light'])) .monogram {
      background: hsl(var(--hue) 30% 26%);
      color: hsl(var(--hue) 60% 82%);
    }
  }
</style>
