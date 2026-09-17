import { ApiError, issues as issuesApi } from '$lib/api';
import type { Guid, IssueSummary, WorkflowStateDto } from '$lib/api/types';
import { issuesIn } from '$lib/board';
import { moveAnchors } from '$lib/dnd.svelte';
import { announce } from '$lib/issues/changes';
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

  // Put it where it was dropped straight away, with a rank between its new neighbours so the
  // optimistic order matches the one the server is about to write.
  const after = column.find((candidate) => candidate.id === anchors.afterIssueId);
  const before = column.find((candidate) => candidate.id === anchors.beforeIssueId);
  const sortOrder = midpoint(after?.sortOrder, before?.sortOrder, column);

  apply(
    issues.map((candidate) =>
      candidate.id === issue.id
        ? {
            ...candidate,
            stateId: target.id,
            stateName: target.name,
            stateType: target.type,
            stateColor: target.color,
            sortOrder
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

/** The rank a card takes between two neighbours — the same midpoint the server computes. */
function midpoint(after: number | undefined, before: number | undefined, column: IssueSummary[]): number {
  if (after !== undefined && before !== undefined) return (after + before) / 2;
  if (after !== undefined) return after + 1;
  if (before !== undefined) return before - 1;

  return column.length === 0 ? 0 : Math.min(...column.map((issue) => issue.sortOrder)) - 1;
}
