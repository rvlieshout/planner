<script lang="ts">
  import { chordKeys, describe, isMac, parse } from '$lib/shortcuts';

  /**
   * A shortcut's caption: one key cap per key, and "then" between the two halves of a sequence.
   *
   * On a Mac the modifiers are glyphs and sit in one cap, `⌘S`, the way the system menus draw them;
   * everywhere else each key gets its own, `Ctrl` `S`.
   */
  interface Props {
    shortcut: string;
  }

  let { shortcut }: Props = $props();

  const chords = $derived(
    parse(shortcut).map((chord) => (isMac ? [chordKeys(chord).join('')] : chordKeys(chord)))
  );
</script>

<span class="shortcut" aria-label={describe(shortcut)}>
  {#each chords as keys, index (index)}
    {#if index > 0}<span class="then">then</span>{/if}
    {#each keys as key, keyIndex (keyIndex)}<kbd>{key}</kbd>{/each}
  {/each}
</span>

<style>
  .shortcut {
    display: inline-flex;
    flex: none;
    align-items: center;
    gap: 3px;
  }

  kbd {
    display: inline-grid;
    place-items: center;
    min-width: 18px;
    height: 18px;
    padding: 0 4px;
    border: 1px solid var(--border);
    border-radius: var(--radius-xs);
    background: var(--bg-sunken);
    color: var(--fg-secondary);
    font-family: var(--font-sans);
    font-size: var(--text-xs);
    line-height: 1;
  }

  .then {
    color: var(--fg-tertiary);
    font-size: var(--text-xs);
  }
</style>
