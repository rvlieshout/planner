<script lang="ts">
  import { goto } from '$app/navigation';
  import { resolve } from '$app/paths';

  /** The application opens on My Issues: the one view about the person rather than the team. */
  $effect(() => {
    void goto(resolve('/my-issues'), { replaceState: true });
  });
</script>
