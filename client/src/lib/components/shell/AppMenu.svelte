<script lang="ts">
  import Icon from '$components/Icon.svelte';
  import Popover from '$components/Popover.svelte';
  import Shortcut from '$components/Shortcut.svelte';
  import type { Command, CommandGroup } from '$lib/commands.svelte';

  /**
   * The application menu — File, View, Project and Help, folded into one button.
   *
   * A desktop application carries a menu bar; a browser tab has one already, at the top of the window,
   * and a second row of words under it reads as a page imitating an application rather than as one.
   * So the same commands live behind a single control, grouped as they would be on a menu bar and
   * captioned with the accelerators that also work without opening it.
   */
  interface Props {
    groups: CommandGroup[];
  }

  let { groups }: Props = $props();

  let open = $state(false);

  function run(command: Command) {
    if (command.disabled) return;

    open = false;
    command.run();
  }
</script>

<Popover {open} onclose={() => (open = false)} width={260}>
  {#snippet trigger()}
    <button
      type="button"
      class="btn btn-quiet btn-icon btn-sm"
      onclick={() => (open = !open)}
      aria-haspopup="menu"
      aria-expanded={open}
      aria-label="Menu">
      <Icon name="menu" size={15} />
    </button>
  {/snippet}

  <div class="menu" role="menu">
    {#each groups as group, index (group.label)}
      {#if index > 0}<hr />{/if}
      <p class="caption group">{group.label}</p>

      {#each group.items as item (item.label)}
        <button
          type="button"
          class="item"
          class:danger={item.danger}
          role="menuitem"
          disabled={item.disabled}
          onclick={() => run(item)}>
          {#if item.icon}
            <Icon name={item.icon} size={14} />
          {:else}
            <span class="glyph"></span>
          {/if}
          <span class="label truncate">{item.label}</span>
          {#if item.shortcut}<Shortcut shortcut={item.shortcut} />{/if}
        </button>
      {/each}
    {/each}
  </div>
</Popover>

<style>
  .menu {
    min-width: 252px;
  }

  .group {
    padding: var(--s-2) var(--s-3) var(--s-1);
  }

  .item {
    display: flex;
    align-items: center;
    gap: var(--s-3);
    width: 100%;
    height: var(--row-h);
    padding: 0 var(--s-3);
    border: 0;
    border-radius: var(--radius-xs);
    background: none;
    color: var(--fg);
    text-align: left;
    cursor: pointer;
  }

  .item:hover:not(:disabled) {
    background: var(--bg-hover);
  }

  .item:disabled {
    color: var(--fg-disabled);
    cursor: not-allowed;
  }

  .item.danger:hover:not(:disabled) {
    background: var(--danger-bg);
    color: var(--danger);
  }

  .glyph {
    width: 14px;
  }

  .label {
    flex: 1;
  }

  hr {
    margin: var(--s-2) 0;
  }
</style>
