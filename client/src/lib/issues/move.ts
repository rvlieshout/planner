import { ApiError, issues as issuesApi } from '$lib/api';
import type { Guid, IssueSummary, WorkflowStateDto } from '$lib/api/types';
import { issuesIn } from '$lib/board';
import { moveAnchors } from '$lib/dnd.svelte';
import { announce } from '$lib/issues/changes';
import { rankBetween } from '$lib/rank';
import { toasts } from '$components/toast.svelte';

/*
 * Dropping an issue into a workflow state, wherever it was dropped from.
 *
 * The board and the list show the same issues grouped the same way — by state, in rank order — so a
 * drop means the same thing in both, and the rule for what it writes lives here rather than once per
 * view. The move is applied first and told to the server afterwards: a card that hangs where it was
 * until a round trip completes feels broken, and the realtime echo is the same idempotent upsert as
 * any other change, so it only confirms what is already on screen. A refusal puts the view back the
 * way the server sees it.
 */

export interface MoveRequest {
  issue: IssueSummary;
  /** The state dropped into. */
  stateId: Guid;
  /** Where among that state's rows it landed, as the drag reported it. */
  index: number;
  states: WorkflowStateDto[];
  /** Everything the view is showing, which is what the optimistic update is computed from. */
  issues: IssueSummary[];
  /** Replaces the view's issues optimistically, and again if the server refuses. */
  apply: (issues: IssueSummary[]) => void;
}

export async function moveIssue({ issue, stateId, index, states, issues, apply }: MoveRequest): Promise<void> {
  const target = states.find((state) => state.id === stateId);
  if (!target) return;

  const column = issuesIn(issues, stateId);
  const anchors = moveAnchors(column, index, issue.id);

  // Dropped back where it already was: same state, same two neighbours. Nothing to write.
  if (stateId === issue.stateId) {
    const from = issuesIn(issues, issue.stateId);
    const current = from.findIndex((candidate) => candidate.id === issue.id);

    if (
      (from[current - 1]?.id ?? null) === anchors.afterIssueId &&
      (from[current + 1]?.id ?? null) === anchors.beforeIssueId
    ) {
      return;
    }
  }

  const previous = issues;

  // Put it where it was dropped straight away, with the rank the server is about to write, so the
  // realtime echo confirms the order on screen rather than rearranging it.
  const rank = rankFor(
    column.filter((candidate) => candidate.id !== issue.id && !candidate.archivedAt),
    anchors.afterIssueId,
    anchors.beforeIssueId
  );

  apply(
    issues.map((candidate) =>
      candidate.id === issue.id
        ? {
            ...candidate,
            stateId: target.id,
            stateName: target.name,
            stateType: target.type,
            stateColor: target.color,
            rank
          }
        : candidate
    )
  );

  try {
    announce(
      'Updated',
      await issuesApi.move(issue.id, {
        stateId: target.id,
        afterIssueId: anchors.afterIssueId,
        beforeIssueId: anchors.beforeIssueId
      })
    );
  } catch (failure) {
    apply(previous);
    toasts.error(failure instanceof ApiError ? failure.message : 'That move was refused.');
  }
}

/**
 * The rank a dropped card takes — the same rule `ResolveRankAsync` applies on the server.
 *
 * It follows the card above it and takes a key before whatever comes next in the column, rather than
 * trusting the card below as well: two cards can share a rank after concurrent drops, and a key
 * "between" equal ranks does not exist. `others` is the column in rank order, without the card itself.
 */
function rankFor(others: IssueSummary[], afterId: Guid | null, beforeId: Guid | null): string {
  const after = others.find((candidate) => candidate.id === afterId);
  if (after) return rankBetween(after.rank, others.find((candidate) => candidate.rank > after.rank)?.rank);

  const before = others.find((candidate) => candidate.id === beforeId);
  if (before) return rankBetween(others.findLast((candidate) => candidate.rank < before.rank)?.rank, before.rank);

  return rankBetween(others.at(-1)?.rank, null);
}
