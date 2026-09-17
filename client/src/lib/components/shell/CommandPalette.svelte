<script lang="ts">
  import { Command } from 'bits-ui';
  import { tick } from 'svelte';
  import Icon from '$components/Icon.svelte';
  import Shortcut from '$components/Shortcut.svelte';
  import { commands, type Command as AppCommand, type CommandGroup } from '$lib/commands.svelte';
  import { matches, parse } from '$lib/shortcuts';

  /**
   * Every command the shell and the open page offer, searchable, with the key each one is bound to.
   *
   * It is the keyboard reference as much as the launcher: opened with nothing typed it lists exactly
   * what works on this screen right now, the page's own commands first. The list and its filtering
   * are Bits UI's Command; the frame is a native <dialog>, like every other modal here, for the top
   * layer, the focus trap and Escape.
   */
  interface Props {
    groups: CommandGroup[];
  }

  let { groups }: Props = $props();

  let dialog = $state<HTMLDialogElement | null>(null);
  let search = $state('');
  let input = $state<HTMLInputElement | null>(null);

  /*
   * The palette filters on each item's value, which must be unique. A label is what someone searches
   * for, so it is the value — qualified by its group only where two groups use the same words.
   */
  const entries = $derived.by(() => {
    const counts = new Map<string, number>();
    for (const item of groups.flatMap((group) => group.items)) {
      counts.set(item.label, (counts.get(item.label) ?? 0) + 1);
    }

    return groups
      .filter((group) => group.items.length > 0)
      .map((group) => ({
        ...group,
        items: group.items.map((item) => ({
          item,
          value: counts.get(item.label)! > 1 ? `${item.label} (${group.label})` : item.label
        }))
      }));
  });

  $effect(() => {
    if (!dialog) return;

    if (commands.paletteOpen && !dialog.open) {
      search = '';
      dialog.showModal();
      void tick().then(() => input?.focus());
    } else if (!commands.paletteOpen && dialog.open) {
      dialog.close();
    }
  });

  function run(command: AppCommand) {
    if (command.disabled) return;

    // Closed first: a command that opens a dialog of its own needs the top layer to itself.
    commands.closePalette();
    command.run();
  }

  function onKeyDown(event: KeyboardEvent) {
    // The key that opened it closes it again.
    if (matches(parse('mod+k')[0], event)) {
      event.preventDefault();
      commands.closePalette();
    }
  }

  function onBackdrop(event: MouseEvent) {
    if (event.target === dialog) commands.closePalette();
  }
</script>

<dialog
  bind:this={dialog}
  class="palette"
  aria-label="Commands"
  oncancel={(event) => {
    event.preventDefault();
    commands.closePalette();
  }}
  onclick={onBackdrop}
  onkeydown={onKeyDown}>
  {#if commands.paletteOpen}
    <Command.Root label="Commands" loop vimBindings={false}>
      <div class="search">
        <Icon name="search" size={15} />
        <Command.Input
          bind:ref={input}
          bind:value={search}
          class="input-bare"
          placeholder="Type a command…" />
      </div>

      <Command.List class="list">
        <Command.Viewport>
          <Command.Empty class="empty">No command matches “{search}”.</Command.Empty>

          {#each entries as group (group.label)}
            <Command.Group value={group.label}>
              <Command.GroupHeading class="caption heading">{group.label}</Command.GroupHeading>
              <Command.GroupItems>
                {#each group.items as { item, value } (value)}
                  <Command.Item
                    {value}
                    keywords={item.keywords}
                    disabled={item.disabled}
                    class="item {item.danger ? 'danger' : ''}"
                    onSelect={() => run(item)}>
                    {#if item.icon}
                      <Icon name={item.icon} size={14} />
                    {:else}
                      <span class="glyph"></span>
                    {/if}
                    <span class="label truncate">{item.label}</span>
                    {#if item.shortcut}<Shortcut shortcut={item.shortcut} />{/if}
                  </Command.Item>
                {/each}
              </Command.GroupItems>
            </Command.Group>
          {/each}
        </Command.Viewport>
      </Command.List>

      <footer>
        <span><Shortcut shortcut="arrowup" /><Shortcut shortcut="arrowdown" /> Move</span>
        <span><Shortcut shortcut="enter" /> Run</span>
        <span><Shortcut shortcut="escape" /> Close</span>
      </footer>
    </Command.Root>
  {/if}
</dialog>

<style>
  .palette {
    width: min(92vw, 560px);
    max-height: min(72vh, 520px);
    margin: 12vh auto auto;
    padding: 0;
    overflow: hidden;
    border: 1px solid var(--border);
    border-radius: var(--radius-lg);
    background: var(--bg-surface);
    color: var(--fg);
    box-shadow: var(--shadow-lg);
  }

  .palette[open] {
    display: flex;
    flex-direction: column;
  }

  .palette::backdrop {
    background: rgb(10 11 14 / 35%);
  }

  .palette :global([data-command-root]) {
    display: flex;
    flex-direction: column;
    min-height: 0;
  }

  .search {
    display: flex;
    flex: none;
    align-items: center;
    gap: var(--s-3);
    height: 44px;
    padding: 0 var(--s-5);
    border-bottom: 1px solid var(--border);
    color: var(--fg-tertiary);
  }

  .search :global(input) {
    flex: 1;
    min-width: 0;
    height: 100%;
    color: var(--fg);
    font-size: var(--text-base);
  }

  .palette :global(.list) {
    flex: 1;
    min-height: 0;
    padding: var(--s-2);
    overflow-y: auto;
  }

  .palette :global(.heading) {
    padding: var(--s-3) var(--s-3) var(--s-1);
  }

  .palette :global(.item) {
    display: flex;
    align-items: center;
    gap: var(--s-3);
    height: var(--row-h);
    padding: 0 var(--s-3);
    border-radius: var(--radius-xs);
    color: var(--fg);
    cursor: pointer;
  }

  .palette :global(.item[data-selected]) {
    background: var(--bg-hover);
  }

  .palette :global(.item.danger[data-selected]) {
    background: var(--danger-bg);
    color: var(--danger);
  }

  .palette :global(.item[data-disabled]) {
    color: var(--fg-disabled);
    cursor: not-allowed;
  }

  .palette :global(.empty) {
    padding: var(--s-6) var(--s-3);
    color: var(--fg-tertiary);
    text-align: center;
  }

  .glyph {
    width: 14px;
  }

  .label {
    flex: 1;
  }

  footer {
    display: flex;
    flex: none;
    gap: var(--s-5);
    padding: var(--s-3) var(--s-5);
    border-top: 1px solid var(--border);
    background: var(--bg-sunken);
    color: var(--fg-tertiary);
    font-size: var(--text-xs);
  }

  footer span {
    display: inline-flex;
    align-items: center;
    gap: var(--s-2);
  }
</style>
