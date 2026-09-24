<script lang="ts">
  import { all, ApiError, comments as commentsApi, issues as issuesApi } from '$lib/api';
  import type { CommentDto, Guid } from '$lib/api/types';
  import { session } from '$lib/auth/session.svelte';
  import { realtime } from '$lib/realtime/hub.svelte';
  import { formatExact, relativeTime } from '$lib/format';
  import Avatar from '$components/Avatar.svelte';
  import Icon from '$components/Icon.svelte';
  import Markdown from '$components/markdown/Markdown.svelte';
  import MarkdownEditor from '$components/markdown/MarkdownEditor.svelte';
  import { confirm } from '$components/confirm.svelte';
  import { toasts } from '$components/toast.svelte';

  /**
   * The conversation on one issue, threaded one level deep — which is what the server stores.
   *
   * Every page is loaded rather than the first: a comment thread is read from the top down, and
   * "load more" on a discussion of eleven replies is a button that exists only because the list was
   * paged for a different reason.
   *
   * A draft in the box is never replaced by a refresh. Someone else's comment arriving while you are
   * halfway through writing yours must not take the words out of the field.
   */
  interface Props {
    issueId: Guid;
    /**
     * False for a reader with no comment permission, and on an archived issue — the box and the edit
     * and delete buttons are not offered rather than refused.
     */
    canComment: boolean;
    /** True while there is an unposted draft, so the page's own guard can mention it. */
    ondraft?: (hasDraft: boolean) => void;
  }

  let { issueId, canComment, ondraft }: Props = $props();

  let comments = $state<CommentDto[]>([]);
  let loading = $state(true);
  let draft = $state('');
  let replyTo = $state<CommentDto | null>(null);
  let posting = $state(false);
  let uploading = $state(false);
  let editUploading = $state(false);
  let editing = $state<Guid | null>(null);
  let editDraft = $state('');
  let error = $state<string | null>(null);

  const threads = $derived(
    comments
      .filter((comment) => !comment.parentCommentId)
      .map((comment) => ({
        comment,
        replies: comments
          .filter((reply) => reply.parentCommentId === comment.id)
          .sort((a, b) => a.createdAt.localeCompare(b.createdAt))
      }))
      .sort((a, b) => a.comment.createdAt.localeCompare(b.comment.createdAt))
  );

  async function load() {
    loading = true;

    try {
      comments = await all((page, pageSize) => issuesApi.comments(issueId, { page, pageSize }));
      error = null;
    } catch (failure) {
      error = failure instanceof ApiError ? failure.message : 'Could not load the comments.';
    } finally {
      loading = false;
    }
  }

  $effect(() => {
    void issueId;
    void load();
  });

  $effect(() => {
    ondraft?.(draft.trim().length > 0 || uploading || editUploading);
  });

  // Comment traffic is opt-in, per issue: an open board must not stream every comment in the team.
  $effect(() => {
    const id = issueId;
    void realtime.subscribeToIssue(id);

    return () => void realtime.unsubscribeFromIssue(id);
  });

  $effect(() =>
    realtime.on('CommentChanged', (change) => {
      if (change.issueId !== issueId) return;

      if (change.kind === 'Deleted') {
        comments = comments.filter((comment) => comment.id !== change.id);
        return;
      }

      const entity = change.entity;
      if (!entity) return;

      comments = comments.some((comment) => comment.id === entity.id)
        ? comments.map((comment) => (comment.id === entity.id ? entity : comment))
        : [...comments, entity];
    })
  );

  async function post() {
    const body = draft.trim();
    if (!body || posting || uploading) return;

    posting = true;

    try {
      const created = await issuesApi.comment(issueId, body, replyTo?.id ?? null);

      comments = comments.some((comment) => comment.id === created.id)
        ? comments
        : [...comments, created];

      draft = '';
      replyTo = null;
      error = null;
    } catch (failure) {
      error = failure instanceof ApiError ? failure.message : 'Posting failed.';
    } finally {
      posting = false;
    }
  }

  async function saveEdit(comment: CommentDto) {
    const body = editDraft.trim();
    if (!body || editUploading) return;

    try {
      const saved = await commentsApi.update(comment.id, body);
      comments = comments.map((candidate) => (candidate.id === saved.id ? saved : candidate));
      editing = null;
    } catch (failure) {
      toasts.error(failure instanceof ApiError ? failure.message : 'Saving that edit failed.');
    }
  }

  async function remove(comment: CommentDto) {
    const answer = await confirm.ask({
      title: 'Delete this comment?',
      message: 'It goes for everyone. Replies to it stay.',
      confirmLabel: 'Delete',
      cancelLabel: 'Cancel',
      danger: true
    });

    if (!answer) return;

    try {
      await commentsApi.remove(comment.id);
      comments = comments.filter((candidate) => candidate.id !== comment.id);
    } catch (failure) {
      toasts.error(failure instanceof ApiError ? failure.message : 'Deleting that comment failed.');
    }
  }

  /** Editing is the author's alone — authorship is not an administrative power. Deleting is not. */
  const mine = (comment: CommentDto) => comment.author.id === session.user?.id;

  function onKeyDown(event: KeyboardEvent) {
    if ((event.ctrlKey || event.metaKey) && event.key === 'Enter') {
      event.preventDefault();
      void post();
    }
  }
</script>

<section class="comments">
  <header class="section-header">
    <h2>Comments</h2>
    <span class="badge">{comments.length}</span>
  </header>

  {#if error}
    <div class="alert alert-error"><Icon name="circle-alert" size={14} /><span>{error}</span></div>
  {/if}

  {#if loading && comments.length === 0}
    <p class="muted">Loading…</p>
  {/if}

  <ol class="threads">
    {#each threads as thread (thread.comment.id)}
      <li>
        {@render comment(thread.comment)}

        {#if thread.replies.length > 0}
          <ol class="replies">
            {#each thread.replies as reply (reply.id)}
              <li>{@render comment(reply)}</li>
            {/each}
          </ol>
        {/if}
      </li>
    {/each}
  </ol>

  {#if canComment}
    <div class="composer">
      {#if replyTo}
        <p class="replying">
          <Icon name="corner-down-right" size={12} />
          Replying to {replyTo.author.displayName}
          <button type="button" class="btn-link" onclick={() => (replyTo = null)}>Cancel</button>
        </p>
      {/if}

      <MarkdownEditor
        bind:value={draft} bind:uploading
        issueId={issueId}
        rows={3}
        placeholder="Leave a comment…"
        disabled={posting}
        onkeydown={onKeyDown} />

      <div class="composer-actions">
        <span class="muted hint">Markdown · paste an image to attach it · Ctrl+Enter posts</span>
        <button
          type="button"
          class="btn btn-primary btn-sm"
          onclick={() => void post()}
          disabled={posting || uploading || !draft.trim()}>
          {posting ? 'Posting…' : 'Comment'}
        </button>
      </div>
    </div>
  {/if}
</section>

{#snippet comment(item: CommentDto)}
  <article class="comment">
    <Avatar name={item.author.displayName} seed={item.author.email} size={22} />

    <div class="body">
      <p class="byline">
        <strong>{item.author.displayName}</strong>
        <span class="muted" title={formatExact(item.createdAt)}>{relativeTime(item.createdAt)}</span>
        {#if item.editedAt}<span class="muted">· edited</span>{/if}

        <span class="spacer"></span>

        {#if mine(item) && canComment}
          <button
            type="button"
            class="icon-action"
            aria-label="Edit"
            onclick={() => {
              editing = item.id;
              editDraft = item.body;
            }}>
            <Icon name="pencil" size={12} />
          </button>
        {/if}
        {#if canComment && (mine(item) || session.isAdmin)}
          <button type="button" class="icon-action" aria-label="Delete" onclick={() => void remove(item)}>
            <Icon name="trash-2" size={12} />
          </button>
        {/if}
        {#if canComment && !item.parentCommentId}
          <button type="button" class="icon-action" aria-label="Reply" onclick={() => (replyTo = item)}>
            <Icon name="message-square" size={12} />
          </button>
        {/if}
      </p>

      {#if editing === item.id}
        <MarkdownEditor bind:value={editDraft} bind:uploading={editUploading} focusOnMount issueId={issueId} rows={3} placeholder="Edit this comment…" />
        <div class="row-tight">
          <button type="button" class="btn btn-primary btn-sm" disabled={editUploading} onclick={() => void saveEdit(item)}>
            Save
          </button>
          <button type="button" class="btn btn-sm" disabled={editUploading} onclick={() => (editing = null)}>Cancel</button>
        </div>
      {:else}
        <div class="text"><Markdown value={item.body} /></div>
      {/if}
    </div>
  </article>
{/snippet}

<style>
  .comments {
    display: flex;
    flex-direction: column;
    gap: var(--s-5);
  }

  h2 {
    font-size: var(--text-md);
  }

  .threads {
    display: flex;
    flex-direction: column;
    gap: var(--s-5);
  }

  .replies {
    display: flex;
    flex-direction: column;
    gap: var(--s-4);
    margin-top: var(--s-4);
    margin-left: var(--s-8);
    padding-left: var(--s-5);
    border-left: 2px solid var(--split);
  }

  .comment {
    display: flex;
    gap: var(--s-4);
  }

  .body {
    display: flex;
    flex: 1;
    flex-direction: column;
    gap: var(--s-2);
    min-width: 0;
  }

  .byline {
    display: flex;
    align-items: baseline;
    gap: var(--s-3);
    font-size: var(--text-sm);
  }

  /* Markdown now, so the line breaks and the wrapping are the document's own — see markdown.css. */
  .text {
    overflow-wrap: anywhere;
  }

  .icon-action {
    display: grid;
    place-items: center;
    width: 20px;
    height: 20px;
    border: 0;
    border-radius: var(--radius-xs);
    background: none;
    color: var(--fg-tertiary);
    cursor: pointer;
    opacity: 0;
  }

  .comment:hover .icon-action,
  .icon-action:focus-visible {
    opacity: 1;
  }

  .icon-action:hover {
    background: var(--bg-hover);
    color: var(--fg);
  }

  .composer {
    display: flex;
    flex-direction: column;
    gap: var(--s-3);
    padding-top: var(--s-5);
    border-top: 1px solid var(--split);
  }

  .replying {
    display: flex;
    align-items: center;
    gap: var(--s-2);
    color: var(--fg-tertiary);
    font-size: var(--text-sm);
  }

  .composer-actions {
    display: flex;
    align-items: center;
    justify-content: flex-end;
    gap: var(--s-4);
  }

  .hint {
    font-size: var(--text-xs);
  }
</style>
