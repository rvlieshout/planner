<script lang="ts">
  import { page } from '$app/state';
  import ProjectEditor from '$components/projects/ProjectEditor.svelte';

  /** An existing project's settings, and the place its milestones are maintained. */
</script>

<ProjectEditor projectId={page.params.id} />
