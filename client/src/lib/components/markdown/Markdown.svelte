<script lang="ts">
  import { reader } from '$lib/markdown/carta';
  import { resolveAttachments } from '$lib/markdown/attachments';

  /**
   * One rendered markdown document.
   *
   * Rendering is asynchronous — Carta's pipeline is — so the previous document stays on screen until
   * the next one is ready rather than blinking through an empty box. Attachment references are filled
   * in after each render, against the element this actually put on the page.
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
</script>

{#if html}
  <div bind:this={container} class="prose">
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
