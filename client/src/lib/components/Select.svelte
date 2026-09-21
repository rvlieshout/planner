<script lang="ts" generics="T">
  import type { Snippet } from 'svelte';
  import Icon from './Icon.svelte';
  import Avatar from './Avatar.svelte';
  import Popover from './Popover.svelte';
  import type { SelectOption } from './select';

  /**
   * The property picker: a chip that *is* its own label.
   *
   * There is no caption column beside these. The icon and the words in the chip say what the value
   * means, which only works if a chip always reads as a value — so every optional property gets an
   * explicit empty option ("No priority", "Unassigned", "No project") rather than an empty box. Those
   * options earn their place twice over: they are also the only way back to empty.
   */
  interface Props {
    options: SelectOption<T>[];
    value: T;
    onchange: (value: T) => void;
    /** Shown when no option matches the current value, which normally means it is still loading. */
    placeholder?: string;
    /** `pill` sizes to its content; `field` fills its column like an input. */
    variant?: 'pill' | 'field';
    disabled?: boolean;
    /** Forced on or off; left unset, a list long enough to need one gets one. */
    searchable?: boolean;
    label?: string;
    id?: string;
    /**
     * A control of the caller's own in place of the chip — a row's avatar, say.
     *
     * The list, the search and the keys are the same; only what opens them differs. The snippet is
     * handed everything a trigger needs, and `onkeydown` is passed straight through so a custom
     * control still opens on Enter and walks the list with the arrows.
     */
    trigger?: Snippet<
      [
        {
          open: boolean;
          selected: SelectOption<T> | undefined;
          toggle: () => void;
          onkeydown: (event: KeyboardEvent) => void;
        }
      ]
    >;
  }

  let {
    options,
    value,
    onchange,
    placeholder = 'Select…',
    variant = 'pill',
    disabled = false,
    searchable,
    label,
    id,
    trigger
  }: Props = $props();

  let open = $state(false);
  let search = $state('');
  let searchInput = $state<HTMLInputElement | null>(null);
  let activeIndex = $state(0);

  const selected = $derived(options.find((option) => Object.is(option.value, value)));
  const showSearch = $derived(searchable ?? options.length > 8);

  const visible = $derived(
    search.trim()
      ? options.filter((option) => option.label.toLowerCase().includes(search.trim().toLowerCase()))
      : options
  );

  function toggle() {
    if (disabled) return;

    open = !open;

    if (open) {
      search = '';
      activeIndex = Math.max(
        0,
        options.findIndex((option) => Object.is(option.value, value))
      );
    }
  }

  function choose(option: SelectOption<T>) {
    if (option.disabled) return;

    open = false;
    onchange(option.value);
  }

  function onKeyDown(event: KeyboardEvent) {
    if (!open) {
      if (event.key === 'Enter' || event.key === ' ' || event.key === 'ArrowDown') {
        event.preventDefault();
        toggle();
      }
      return;
    }

    switch (event.key) {
      case 'ArrowDown':
        event.preventDefault();
        activeIndex = Math.min(activeIndex + 1, visible.length - 1);
        break;
      case 'ArrowUp':
        event.preventDefault();
        activeIndex = Math.max(activeIndex - 1, 0);
        break;
      case 'Home':
        event.preventDefault();
        activeIndex = 0;
        break;
      case 'End':
        event.preventDefault();
        activeIndex = visible.length - 1;
        break;
      case 'Enter':
        event.preventDefault();
        if (visible[activeIndex]) choose(visible[activeIndex]);
        break;
      case 'Tab':
        open = false;
        break;
    }
  }

  $effect(() => {
    if (open && showSearch) searchInput?.focus();
  });
</script>

{#snippet chip()}
  {#if trigger}
    {@render trigger({ open, selected, toggle, onkeydown: onKeyDown })}
  {:else}
    <button
      {id}
      type="button"
      class="trigger {variant === 'field' ? 'as-field' : 'as-pill'}"
      class:open
      {disabled}
      aria-haspopup="listbox"
      aria-expanded={open}
      aria-label={label}
      onclick={toggle}
      onkeydown={onKeyDown}>
      {#if selected}
        {#if selected.avatarName !== undefined}
          <Avatar name={selected.avatarName} seed={selected.avatarSeed} size={16} />
        {:else if selected.icon}
          <span class="glyph" style:color={selected.color}>
            <Icon name={selected.icon} size={14} />
          </span>
        {:else if selected.color}
          <span class="swatch" style:background={selected.color}></span>
        {/if}
        <span class="value truncate">{selected.label}</span>
      {:else}
        <span class="value truncate placeholder">{placeholder}</span>
      {/if}
      <Icon name="chevron-down" size={12} class="caret" />
    </button>
  {/if}
{/snippet}

<Popover
  {open}
  onclose={() => (open = false)}
  width={variant === 'field' ? 'trigger' : undefined}
  trigger={chip}>
  <div class="menu" role="listbox" aria-label={label}>
    {#if showSearch}
      <div class="search">
        <Icon name="search" size={13} />
        <input
          bind:this={searchInput}
          bind:value={search}
          class="search-input"
          type="text"
          placeholder="Search…"
          onkeydown={onKeyDown} />
      </div>
    {/if}

    {#each visible as option, index (String(option.value))}
      <button
        type="button"
        class="option"
        class:active={index === activeIndex}
        class:selected={Object.is(option.value, value)}
        role="option"
        aria-selected={Object.is(option.value, value)}
        disabled={option.disabled}
        onmouseenter={() => (activeIndex = index)}
        onclick={() => choose(option)}>
        {#if option.avatarName !== undefined}
          <Avatar name={option.avatarName} seed={option.avatarSeed} size={18} />
        {:else if option.icon}
          <span class="glyph" style:color={option.color}>
            <Icon name={option.icon} size={14} />
          </span>
        {:else if option.color}
          <span class="swatch" style:background={option.color}></span>
        {:else}
          <span class="glyph"></span>
        {/if}

        <span class="option-text">
          <span class="truncate">{option.label}</span>
          {#if option.hint}<span class="hint truncate">{option.hint}</span>{/if}
        </span>

        {#if Object.is(option.value, value)}
          <Icon name="check" size={13} class="tick" />
        {/if}
      </button>
    {:else}
      <p class="none">No matches.</p>
    {/each}
  </div>
</Popover>

<style>
  .trigger {
    display: inline-flex;
    align-items: center;
    gap: var(--s-2);
    max-width: 100%;
    height: var(--control-h-sm);
    padding: 0 var(--s-2) 0 var(--s-3);
    border: 1px solid transparent;
    border-radius: var(--radius-sm);
    background: var(--bg-sunken);
    color: var(--fg);
    font-size: var(--text-sm);
    cursor: pointer;
    transition: background var(--duration) var(--ease);
  }

  .trigger:hover:not(:disabled),
  .trigger.open {
    background: var(--bg-active);
  }

  .trigger:disabled {
    color: var(--fg-disabled);
    cursor: not-allowed;
  }

  /*
   * Fills its column and wears an input's chrome, for a form with real captions.
   *
   * The class is "as-field" rather than "field" because app.css has a global ".field" that stacks a
   * label over its control in a column. A trigger carrying that class picks up the column direction
   * and lays its own glyph, value and caret out one above the other instead of in a row.
   */
  .trigger.as-field {
    justify-content: flex-start;
    width: 100%;
    height: var(--control-h);
    padding: 0 var(--s-3) 0 var(--s-4);
    border-color: var(--border-strong);
    background: var(--bg-surface);
    font-size: var(--text-base);
  }

  .trigger.as-field:hover:not(:disabled),
  .trigger.as-field.open {
    border-color: var(--accent-border);
    background: var(--bg-surface);
  }

  .value {
    min-width: 0;
  }

  .trigger.as-field .value {
    flex: 1;
    text-align: left;
  }

  .placeholder {
    color: var(--fg-tertiary);
  }

  .trigger :global(.caret) {
    margin-left: auto;
    color: var(--fg-tertiary);
  }

  .glyph,
  .swatch {
    display: inline-flex;
    flex: none;
    align-items: center;
    justify-content: center;
    width: 14px;
  }

  .swatch {
    width: 10px;
    height: 10px;
    border-radius: 3px;
  }

  .menu {
    display: flex;
    flex-direction: column;
    gap: 1px;
    min-width: 180px;
    max-width: 320px;
  }

  .search {
    display: flex;
    align-items: center;
    gap: var(--s-2);
    margin-bottom: var(--s-2);
    padding: 0 var(--s-3);
    border-bottom: 1px solid var(--border);
    color: var(--fg-tertiary);
  }

  .search-input {
    width: 100%;
    height: var(--control-h);
    border: 0;
    background: none;
    outline: none;
    font-size: var(--text-base);
  }

  .option {
    display: flex;
    align-items: center;
    gap: var(--s-3);
    width: 100%;
    min-height: var(--row-h);
    padding: var(--s-2) var(--s-3);
    border: 0;
    border-radius: var(--radius-xs);
    background: none;
    text-align: left;
    cursor: pointer;
  }

  .option.active {
    background: var(--bg-hover);
  }

  .option.selected {
    color: var(--accent);
    font-weight: 500;
  }

  .option:disabled {
    color: var(--fg-disabled);
    cursor: not-allowed;
  }

  .option-text {
    display: flex;
    flex: 1;
    flex-direction: column;
    min-width: 0;
  }

  .hint {
    color: var(--fg-tertiary);
    font-size: var(--text-xs);
    font-weight: 400;
  }

  .option :global(.tick) {
    flex: none;
    color: var(--accent);
  }

  .none {
    padding: var(--s-4);
    color: var(--fg-tertiary);
    font-size: var(--text-sm);
    text-align: center;
  }
</style>
