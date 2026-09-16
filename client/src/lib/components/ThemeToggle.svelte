<script lang="ts">
  import Icon from './Icon.svelte';
  import Popover from './Popover.svelte';
  import { settings, type ThemeChoice } from '$lib/settings.svelte';
  import type { IconName } from '$lib/icons/icons';

  /** Light, dark, or whatever the operating system is doing — which is the default. */
  const CHOICES: { value: ThemeChoice; label: string; icon: IconName }[] = [
    { value: 'system', label: 'Match system', icon: 'settings' },
    { value: 'light', label: 'Light', icon: 'sun' },
    { value: 'dark', label: 'Dark', icon: 'moon' }
  ];

  let open = $state(false);

  const current = $derived(CHOICES.find((c) => c.value === settings.theme) ?? CHOICES[0]);
</script>

<Popover {open} onclose={() => (open = false)} align="end">
  {#snippet trigger()}
    <button
      type="button"
      class="btn btn-quiet btn-icon btn-sm"
      onclick={() => (open = !open)}
      title="Appearance: {current.label}"
      aria-label="Appearance">
      <Icon name={current.icon} size={14} />
    </button>
  {/snippet}

  <div class="menu">
    {#each CHOICES as choice (choice.value)}
      <button
        type="button"
        class="item"
        class:selected={settings.theme === choice.value}
        onclick={() => {
          settings.setTheme(choice.value);
          open = false;
        }}>
        <Icon name={choice.icon} size={14} />
        <span>{choice.label}</span>
        {#if settings.theme === choice.value}<Icon name="check" size={13} class="tick" />{/if}
      </button>
    {/each}
  </div>
</Popover>

<style>
  .menu {
    min-width: 168px;
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
    text-align: left;
    cursor: pointer;
  }

  .item:hover {
    background: var(--bg-hover);
  }

  .item.selected {
    color: var(--accent);
  }

  .item :global(.tick) {
    margin-left: auto;
  }
</style>
