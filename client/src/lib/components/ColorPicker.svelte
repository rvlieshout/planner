<script lang="ts">
  import { SWATCHES } from '$lib/meta';

  /**
   * Ten swatches and a hex box.
   *
   * The swatches are the quick answer and the box is the honest one: the server takes any hex, and a
   * team may have a colour of its own that is not on anyone's palette.
   *
   * Selection is a ring around the swatch rather than a tick drawn on it — a tick is invisible on the
   * pale half of the palette, whichever colour it is drawn in.
   */
  interface Props {
    value: string;
    onchange: (value: string) => void;
    disabled?: boolean;
    label?: string;
  }

  let { value, onchange, disabled = false, label = 'Colour' }: Props = $props();

  const normalized = $derived(value.trim().toLowerCase());

  function onHexInput(event: Event) {
    const input = (event.currentTarget as HTMLInputElement).value.trim();
    const hex = input.startsWith('#') ? input : `#${input}`;

    // Written back only once it is a colour; half-typed input stays in the box without repainting
    // everything that reads from it.
    if (/^#([\da-f]{3}|[\da-f]{6})$/i.test(hex)) onchange(hex);
  }
</script>

<div class="picker" role="group" aria-label={label}>
  <div class="swatches">
    {#each SWATCHES as swatch (swatch)}
      <button
        type="button"
        class="swatch"
        class:selected={normalized === swatch.toLowerCase()}
        style:background={swatch}
        {disabled}
        aria-label={swatch}
        aria-pressed={normalized === swatch.toLowerCase()}
        onclick={() => onchange(swatch)}>
      </button>
    {/each}
  </div>

  <label class="hex">
    <span class="preview" style:background={value}></span>
    <input
      class="input"
      type="text"
      value={value}
      maxlength="7"
      spellcheck="false"
      {disabled}
      aria-label="{label} hex value"
      oninput={onHexInput} />
  </label>
</div>

<style>
  .picker {
    display: flex;
    flex-wrap: wrap;
    align-items: center;
    gap: var(--s-4);
  }

  .swatches {
    display: flex;
    gap: var(--s-2);
  }

  .swatch {
    width: 20px;
    height: 20px;
    border: 0;
    border-radius: var(--radius-sm);
    box-shadow: 0 0 0 1px rgb(0 0 0 / 10%) inset;
    cursor: pointer;
  }

  .swatch.selected {
    outline: 2px solid var(--accent);
    outline-offset: 2px;
  }

  .swatch:disabled {
    cursor: not-allowed;
    opacity: 0.5;
  }

  .hex {
    display: flex;
    align-items: center;
    gap: var(--s-2);
  }

  .preview {
    width: 20px;
    height: 20px;
    border-radius: var(--radius-sm);
    box-shadow: 0 0 0 1px rgb(0 0 0 / 10%) inset;
  }

  .hex .input {
    width: 96px;
    font-family: var(--font-mono);
    font-size: var(--text-sm);
  }
</style>
