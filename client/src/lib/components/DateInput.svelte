<script lang="ts">
  import { regional } from '$lib/regional.svelte';
  import { calendarDate } from '$lib/regional';
  import Icon from './Icon.svelte';

  let { value = $bindable(''), class: className = '', disabled = false, ...rest }:
    { value?: string; class?: string; disabled?: boolean; id?: string; 'aria-label'?: string } = $props();
  let picker: HTMLInputElement;
  const display = $derived(calendarDate(value, regional.locale));
</script>

<!-- The calendar supplies ISO values; the visible button uses our regional preference. -->
<span class="date-input {className}" class:disabled>
  <button {...rest} type="button" class="choose" {disabled}
    aria-label={`${rest['aria-label'] ?? 'Choose date'}: ${display || 'No date'}`}
    onclick={() => picker.showPicker()}>
    <span>{display || 'Choose date'}</span>
    <Icon name="calendar" size={14} />
  </button>
  {#if value}
    <button type="button" class="clear" {disabled} aria-label={`Clear ${rest['aria-label'] ?? 'date'}`}
      onclick={() => value = ''}><Icon name="x" size={12} /></button>
  {/if}
  <input bind:this={picker} type="date" bind:value {disabled} tabindex="-1" aria-hidden="true" />
</span>

<style>
  .date-input {
    position: relative;
    display: inline-flex;
    align-items: center;
    justify-content: space-between;
    gap: var(--s-2);
    min-width: 130px;
  }
  .date-input:focus-within { outline: 2px solid var(--accent); outline-offset: 2px; }
  .disabled { opacity: 0.5; }
  button {
    border: 0;
    padding: 0;
    background: none;
    color: inherit;
    font: inherit;
    cursor: pointer;
  }
  .choose { display: flex; align-items: center; justify-content: space-between; gap: var(--s-2); flex: 1; }
  .clear { display: flex; align-items: center; }
  input {
    position: absolute;
    inset: 0;
    width: 100%;
    height: 100%;
    opacity: 0;
    pointer-events: none;
  }
</style>
