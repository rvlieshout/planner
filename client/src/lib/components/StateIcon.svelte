<script lang="ts">
  import Icon from './Icon.svelte';
  import { STATE_TYPE } from '$lib/meta';
  import type { WorkflowStateType } from '$lib/api/types';

  /**
   * A workflow state's glyph, in the state's own colour.
   *
   * The shape comes from the state *type* and the colour from the state itself, so a renamed or
   * recoloured column still reads as the thing it is — started work looks started whatever the team
   * decided to call it.
   */
  interface Props {
    type: WorkflowStateType;
    color?: string | null;
    size?: number;
    title?: string;
  }

  let { type, color = null, size = 14, title }: Props = $props();

  const meta = $derived(STATE_TYPE[type] ?? STATE_TYPE.Unstarted);
</script>

<span class="state" style:color={color || meta.color} title={title ?? meta.label}>
  <Icon name={meta.icon} {size} />
</span>

<style>
  .state {
    display: inline-flex;
    flex: none;
    align-items: center;
  }
</style>
