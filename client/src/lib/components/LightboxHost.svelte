<script lang="ts">
  import Icon from './Icon.svelte';
  import { lightbox } from './lightbox.svelte';

  /**
   * The viewer behind `lightbox.open()`.
   *
   * A native <dialog> for the same reasons Modal is one — the top layer, a focus trap and Escape as
   * `cancel` — but without Modal's frame: the picture is the whole dialog. It opens fitted to the
   * screen; a click on it shows it at its own pixel size, scrolling where it is larger than the
   * screen, and a click outside it closes.
   */
  let dialog = $state<HTMLDialogElement | null>(null);
  let actual = $state(false);

  const image = $derived(lightbox.current);

  $effect(() => {
    if (!dialog) return;

    if (image && !dialog.open) {
      actual = false;
      dialog.showModal();
    } else if (!image && dialog.open) {
      dialog.close();
    }
  });

  function close(event?: Event) {
    event?.preventDefault();
    lightbox.close();
  }
</script>

<!-- svelte-ignore a11y_click_events_have_key_events, a11y_no_noninteractive_element_interactions -->
<dialog
  bind:this={dialog}
  class="lightbox"
  class:actual
  aria-label={image?.alt || 'Image'}
  oncancel={close}
  onclick={(event) => {
    if (event.target === event.currentTarget || (event.target as HTMLElement).classList.contains('stage')) close();
  }}>
  {#if image}
    <div class="bar">
      <span class="name truncate">{image.alt}</span>
      <button
        type="button"
        class="btn btn-sm btn-quiet"
        onclick={() => (actual = !actual)}
        title={actual ? 'Fit to the screen' : 'Show at actual size'}>
        <Icon name={actual ? 'minimize-2' : 'maximize-2'} size={13} />
        {actual ? 'Fit' : 'Actual size'}
      </button>
      <button type="button" class="btn btn-sm btn-quiet btn-icon" onclick={() => close()} aria-label="Close">
        <Icon name="x" size={14} />
      </button>
    </div>

    <div class="stage">
      <!-- svelte-ignore a11y_click_events_have_key_events, a11y_no_noninteractive_element_interactions -->
      <img src={image.src} alt={image.alt} onclick={() => (actual = !actual)} />
    </div>
  {/if}
</dialog>

<style>
  .lightbox {
    width: 100vw;
    max-width: none;
    height: 100vh;
    max-height: none;
    padding: 0;
    border: 0;
    background: transparent;
    color: #fff;
  }

  .lightbox:not([open]) {
    display: none;
  }

  .lightbox[open] {
    display: flex;
    flex-direction: column;
  }

  .lightbox::backdrop {
    background: rgb(10 11 14 / 82%);
  }

  .bar {
    display: flex;
    flex: none;
    align-items: center;
    gap: var(--s-3);
    height: var(--toolbar-h);
    padding: 0 var(--s-5);
  }

  .bar .btn {
    color: #fff;
  }

  .bar .btn:hover {
    background: rgb(255 255 255 / 12%);
  }

  .name {
    flex: 1;
    min-width: 0;
    font-size: var(--text-sm);
    opacity: 0.85;
  }

  .stage {
    display: grid;
    flex: 1;
    min-height: 0;
    place-items: center;
    padding: 0 var(--s-6) var(--s-6);
    overflow: auto;
  }

  img {
    max-width: 100%;
    max-height: 100%;
    border-radius: var(--radius-sm);
    background: var(--bg-surface);
    box-shadow: var(--shadow-lg);
    cursor: zoom-in;
  }

  /* At its own size the picture may be wider or taller than the screen; the stage scrolls. */
  .actual .stage {
    place-items: start center;
  }

  .actual img {
    max-width: none;
    max-height: none;
    cursor: zoom-out;
  }
</style>
