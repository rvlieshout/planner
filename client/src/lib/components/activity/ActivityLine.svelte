<script lang="ts">
  import type { ActivityEventDto } from '$lib/api/types';
  import { describeActivity } from '$lib/activity';
  import { formatExact, relativeTime } from '$lib/format';
  import Avatar from '$components/Avatar.svelte';
  import Icon from '$components/Icon.svelte';

  /**
   * One audit event as a sentence: who, what, and — in a feed that spans issues — which issue.
   *
   * The wording comes from `describeActivity`, so every list that shows events says the same thing
   * about the same change. The line is text, not a control; the list around it decides what clicking
   * a row does, because the inbox and the feed want different things from it.
   */
  interface Props {
    event: ActivityEventDto;
    /** Names the issue after the sentence. Off on the issue's own page, where it is the page. */
    showIssue?: boolean;
    /** Draws the event's icon instead of the actor's avatar — the compact form an issue history uses. */
    compact?: boolean;
  }

  let { event, showIssue = true, compact = false }: Props = $props();

  const described = $derived(describeActivity(event));
</script>

<span class="line" class:compact>
  {#if compact}
    <span class="glyph"><Icon name={described.icon} size={13} /></span>
  {:else}
    <Avatar name={event.actor.displayName} src={event.actor.avatarUrl} seed={event.actor.email} size={22} />
  {/if}

  <span class="sentence">
    <strong>{event.actor.displayName}</strong>
    {described.text}
    {#if showIssue && event.issue}
      <span class="issue">
        <span class="issue-key">{event.issue.key}</span>
        <span class="title">{event.issue.title}</span>
      </span>
    {/if}
  </span>

  <time class="muted" datetime={event.createdAt} title={formatExact(event.createdAt)}>
    {relativeTime(event.createdAt)}
  </time>
</span>

<style>
  .line {
    display: flex;
    align-items: center;
    gap: var(--s-4);
    min-width: 0;
    font-size: var(--text-sm);
  }

  .line.compact {
    gap: var(--s-3);
    color: var(--fg-secondary);
  }

  .glyph {
    display: grid;
    flex: none;
    place-items: center;
    width: 22px;
    height: 22px;
    border: 1px solid var(--split);
    border-radius: 50%;
    background: var(--bg-surface);
    color: var(--fg-tertiary);
  }

  .sentence {
    flex: 1;
    min-width: 0;
    overflow: hidden;
    text-overflow: ellipsis;
    white-space: nowrap;
  }

  .compact .sentence {
    white-space: normal;
  }

  strong {
    color: var(--fg);
    font-weight: 600;
  }

  .issue {
    margin-left: var(--s-2);
  }

  .issue .title {
    margin-left: var(--s-2);
    color: var(--fg);
  }

  time {
    flex: none;
    font-size: var(--text-xs);
    font-variant-numeric: tabular-nums;
  }
</style>
