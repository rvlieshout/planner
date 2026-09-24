<script lang="ts">
  import { ApiError, issues as issuesApi } from '$lib/api';
  import type { Guid, UserSummary } from '$lib/api/types';
  import { Permission, session } from '$lib/auth/session.svelte';
  import { onIssueChange } from '$lib/issues/changes';
  import { realtime } from '$lib/realtime/hub.svelte';
  import { workspace } from '$lib/workspace.svelte';
  import Avatar from '$components/Avatar.svelte';
  import Icon from '$components/Icon.svelte';
  import Select from '$components/Select.svelte';
  import type { SelectOption } from '$components/select';
  import { toasts } from '$components/toast.svelte';

  /**
   * Who hears about this issue.
   *
   * Anyone who can read it can follow or unfollow it. Someone who can edit it can also add a teammate
   * — the way to pull a person into an issue they have not touched — or take one off. Filing the issue,
   * being assigned it and commenting on it all follow it automatically, on the server, so the list is
   * re-read when one of those happens rather than guessed at here.
   *
   * An archived issue gains no followers. Leaving one still works — it only changes your own inbox.
   */
  interface Props {
    issueId: Guid;
    teamId: Guid;
    archived?: boolean;
  }

  let { issueId, teamId, archived = false }: Props = $props();

  let followers = $state<UserSummary[]>([]);
  let busy = $state(false);

  const me = $derived(session.user?.id);
  const following = $derived(followers.some((follower) => follower.id === me));
  const canManage = $derived(session.can(teamId, Permission.Write));
  const canAdd = $derived(canManage && !archived);

  const addable = $derived<SelectOption<Guid>[]>(
    workspace
      .membersNow(teamId)
      .filter((member) => !followers.some((follower) => follower.id === member.userId))
      .sort((a, b) => a.displayName.localeCompare(b.displayName))
      .map((member) => ({
        value: member.userId,
        label: member.displayName,
        avatarName: member.displayName,
        avatarSeed: member.email,
        hint: member.email
      }))
  );

  let generation = 0;

  async function load() {
    const id = issueId;
    const mine = ++generation;

    try {
      const loaded = await issuesApi.subscribers(id);
      if (mine === generation) followers = loaded;
    } catch {
      /* The list is secondary; the issue itself is what the page is for. */
    }
  }

  $effect(() => {
    void issueId;
    void load();
  });

  // The server follows people who take part. Those moments are visible here as an issue change — an
  // assignee, including this page's own save — or a new comment.
  $effect(() =>
    onIssueChange((change) => {
      if (change.id === issueId) void load();
    })
  );

  $effect(() =>
    realtime.on('CommentChanged', (change) => {
      if (change.issueId === issueId && change.kind === 'Created') void load();
    })
  );

  $effect(() => realtime.onReconnected(() => void load()));

  async function change(userId: Guid, follow: boolean) {
    busy = true;

    try {
      followers = follow ? await issuesApi.subscribe(issueId, userId) : await issuesApi.unsubscribe(issueId, userId);
    } catch (failure) {
      toasts.error(failure instanceof ApiError ? failure.message : 'That could not be changed.');
    } finally {
      busy = false;
    }
  }
</script>

<section class="followers">
  <header class="section-header">
    <h3 class="caption">Followers</h3>

    {#if canAdd}
      <Select options={addable} value={''} onchange={(userId) => void change(userId, true)} label="Add a follower">
        {#snippet trigger({ open, toggle, onkeydown })}
          <button
            type="button"
            class="btn btn-quiet btn-icon btn-sm"
            class:open
            aria-haspopup="listbox"
            aria-expanded={open}
            title="Add a follower"
            aria-label="Add a follower"
            disabled={busy}
            onclick={() => {
              void workspace.membersFor(teamId);
              toggle();
            }}
            {onkeydown}>
            <Icon name="user-plus" size={13} />
          </button>
        {/snippet}
      </Select>
    {/if}
  </header>

  {#if following || !archived}
    <button
      type="button"
      class="btn btn-sm btn-block"
      disabled={busy || !me}
      onclick={() => me && void change(me, !following)}>
      <Icon name={following ? 'bell-off' : 'bell'} size={13} />
      {following ? 'Unfollow' : 'Follow'}
    </button>
  {/if}

  <ul class="list">
    {#each followers as follower (follower.id)}
      <li>
        <Avatar name={follower.displayName} src={follower.avatarUrl} seed={follower.email} size={18} />
        <span class="truncate name">
          {follower.displayName}{#if follower.id === me}<span class="muted"> (you)</span>{/if}
        </span>

        {#if canManage && follower.id !== me}
          <button
            type="button"
            class="btn btn-quiet btn-icon btn-sm remove"
            disabled={busy}
            onclick={() => void change(follower.id, false)}
            aria-label="Remove {follower.displayName}"
            title="Remove {follower.displayName}">
            <Icon name="x" size={12} />
          </button>
        {/if}
      </li>
    {:else}
      <li class="none muted">{archived ? 'Nobody follows this archived issue.' : 'Nobody follows this issue.'}</li>
    {/each}
  </ul>
</section>

<style>
  .followers {
    display: flex;
    flex-direction: column;
    gap: var(--s-3);
  }

  .list {
    display: flex;
    flex-direction: column;
    gap: var(--s-1);
  }

  li {
    display: flex;
    align-items: center;
    gap: var(--s-3);
    min-height: var(--row-h-sm);
    padding: 0 var(--s-2);
    font-size: var(--text-sm);
  }

  .name {
    flex: 1;
    min-width: 0;
  }

  .remove {
    opacity: 0;
  }

  li:hover .remove,
  .remove:focus-visible {
    opacity: 1;
  }

  .none {
    font-size: var(--text-sm);
  }
</style>
