<script lang="ts">
  import { TIME_ZONES } from '$lib/regional';

  let { id, value = $bindable('UTC'), disabled = false }:
    { id: string; value?: string; disabled?: boolean } = $props();

  const supported = $derived(TIME_ZONES.some((zone) => zone.value === value));
</script>

<select {id} bind:value class="input" {disabled}>
  {#if !supported}
    <!-- Preserve an older profile until the user explicitly chooses a replacement. -->
    <option value={value} disabled>{value} (saved timezone)</option>
  {/if}
  {#each TIME_ZONES as zone (zone.value)}
    <option value={zone.value}>{zone.label}</option>
  {/each}
</select>
