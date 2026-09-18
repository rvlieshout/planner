<script lang="ts">
  import type { ProjectDto } from '$lib/api/types';

  /**
   * Which project an issue belongs to: a small dot in the project's colour and its name, set in the
   * same muted tone as the rest of a card's metadata.
   *
   * No pill and no tint. This is context you want available while scanning, not another chip
   * competing with the labels — the dot carries the colour, the text stays quiet.
   *
   * A span rather than a link: every place it appears sits inside the card or row's own button, and
   * a nested button is neither valid nor clickable.
   */
  interface Props {
    project: ProjectDto;
    /** Just the dot, for somewhere the name has no room — the title still names it. */
    compact?: boolean;
  }

  let { project, compact = false }: Props = $props();
</script>

{#if compact}
  <span class="dot" style:background={project.color} title={project.name}></span>
{:else}
  <span class="project-chip" title="Project: {project.name}">
    <span class="dot" style:background={project.color}></span>
    <span class="truncate">{project.name}</span>
  </span>
{/if}

<style>
  .project-chip {
    display: inline-flex;
    align-items: center;
    gap: var(--s-2);

    /* Shrinkable, so a long project name gives way to the title rather than pushing it out. */
    min-width: 0;
    max-width: 150px;
    color: var(--fg-tertiary);
    font-size: var(--text-xs);
    white-space: nowrap;
  }

  .dot {
    flex: none;
    width: 6px;
    height: 6px;
    border-radius: 50%;

    /* The one spot of colour, dimmed just enough not to read as a status light. */
    opacity: 0.8;
  }
</style>
