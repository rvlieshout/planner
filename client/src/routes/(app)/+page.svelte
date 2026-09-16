<script lang="ts">
  import { goto } from '$app/navigation';
  import { base } from '$app/paths';

  /** The application opens on My Issues: the one view about the person rather than the team. */
  $effect(() => {
    void goto(`${base}/my-issues`, { replaceState: true });
  });
</script>
