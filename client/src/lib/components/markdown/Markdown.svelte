<script lang="ts">
  import { reader } from '$lib/markdown/carta';
  import { resolveAttachments } from '$lib/markdown/attachments';
  import { lightbox } from '$components/lightbox.svelte';

  /**
   * One rendered markdown document.
   *
   * Rendering is asynchronous — Carta's pipeline is — so the previous document stays on screen until
   * the next one is ready rather than blinking through an empty box. Attachment references are filled
   * in after each render, against the element this actually put on the page. A click on an image
   * that has loaded opens it in the lightbox.
   */
  interface Props {
    value: string | null | undefined;
    /** Shown instead of the document when there is nothing to show. */
    placeholder?: string;
  }

  let { value, placeholder }: Props = $props();

  let html = $state('');
  let container = $state<HTMLElement | null>(null);

  $effect(() => {
    const source = value ?? '';

    if (!source.trim()) {
      html = '';
      return;
    }

    let current = true;

    void reader.render(source).then((rendered) => {
      if (current) html = rendered;
    });

    return () => {
      current = false;
    };
  });

  // After the render lands in the DOM, not before: the resolver works on elements, not on a string.
  $effect(() => {
    void html;
    if (container) resolveAttachments(container);
  });

  function onclick(event: MouseEvent) {
    const image = (event.target as HTMLElement).closest('img');
    if (!image || image.dataset.resolved === 'pending' || !image.src) return;

    // The document may sit inside something clickable — a description opens its editor on a click.
    event.stopPropagation();
    lightbox.open({ src: image.src, alt: image.alt });
  }
</script>

{#if html}
  <!-- svelte-ignore a11y_click_events_have_key_events, a11y_no_static_element_interactions -->
  <div bind:this={container} class="prose" {onclick}>
    <!-- eslint-disable-next-line svelte/no-at-html-tags -- sanitised in carta.ts, on every surface -->
    {@html html}
  </div>
{:else if placeholder}
  <p class="prose-placeholder">{placeholder}</p>
{/if}

<style>
  .prose-placeholder {
    margin: 0;
    color: var(--fg-tertiary);
    font-size: var(--text-base);
  }
</style>
