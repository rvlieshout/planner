import type { ChangeKind, EntityChange, IssueSummary } from '$lib/api/types';
import { realtime } from '$lib/realtime/hub.svelte';

/*
 * Issue changes, from either direction.
 *
 * A board applies the result of a save straight away rather than waiting for the socket to echo it
 * back — a card that hangs where it was until a round trip completes feels broken. The echo then
 * arrives and changes nothing, because upserting by id is idempotent, which is also what makes a
 * duplicate delivery or a replay after reconnecting harmless.
 *
 * So both sources are funnelled through one subscription and every view handles them the same way.
 */

type Listener = (change: EntityChange<IssueSummary>) => void;

const listeners = new Set<Listener>();

/**
 * The actor on a change this client announced itself.
 *
 * Never a real user id, and deliberately not *this* user's id either: a view needs to tell "I already
 * applied this" apart from "the same person changed it in another tab", and those want opposite
 * treatment — skip the first, refetch for the second.
 */
export const LOCAL_ACTOR = '';

/** True for an echo of something this page just did, as opposed to news from the server. */
export function isLocalEcho(change: EntityChange<unknown>): boolean {
  return change.actorId === LOCAL_ACTOR;
}

/** Announces a change this client just made, so open views apply it without a refetch. */
export function announce(kind: ChangeKind, issue: IssueSummary): void {
  const change: EntityChange<IssueSummary> = {
    kind,
    entityType: 'issue',
    id: issue.id,
    teamId: issue.teamId,
    projectId: issue.projectId,
    issueId: issue.id,
    actorId: LOCAL_ACTOR,
    occurredAt: new Date().toISOString(),
    entity: issue
  };

  for (const listener of listeners) listener(change);
}

/**
 * Subscribes to every issue change — local and remote. Returns the unsubscribe function.
 *
 * Views call this from an `$effect`, so the teardown runs when the view goes away and a slow response
 * can never write into a page that has been replaced.
 */
export function onIssueChange(listener: Listener): () => void {
  listeners.add(listener);
  const off = realtime.on('IssueChanged', listener);

  return () => {
    listeners.delete(listener);
    off();
  };
}

/**
 * Applies one change to a list of issues, in place.
 *
 * `Created` and `Updated` are the same upsert; `Archived` and `Deleted` remove the row. `belongs`
 * decides whether the issue is one this view should be holding at all, so a card that is moved out of
 * a project — or into one — appears and disappears from the right lists without either refetching.
 */
export function applyChange(
  issues: IssueSummary[],
  change: EntityChange<IssueSummary>,
  belongs: (issue: IssueSummary) => boolean
): IssueSummary[] {
  if (change.kind === 'Deleted' || change.kind === 'Archived') {
    return issues.filter((issue) => issue.id !== change.id);
  }

  const entity = change.entity;
  if (!entity) return issues;

  if (!belongs(entity)) {
    return issues.some((issue) => issue.id === entity.id)
      ? issues.filter((issue) => issue.id !== entity.id)
      : issues;
  }

  const index = issues.findIndex((issue) => issue.id === entity.id);

  return index >= 0
    ? issues.map((issue) => (issue.id === entity.id ? entity : issue))
    : [...issues, entity];
}
