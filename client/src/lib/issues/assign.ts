import { ApiError, issues as issuesApi } from '$lib/api';
import type { IssueSummary, TeamMemberDto } from '$lib/api/types';
import { announce } from '$lib/issues/changes';
import { toasts } from '$components/toast.svelte';

/*
 * Handing an issue to someone, from wherever its assignee is drawn.
 *
 * A board row and a board card show the same person the same way, so reassigning from either means
 * the same thing, and the rule for what it writes lives here rather than once per view — the same
 * split `move.ts` makes for a drop.
 *
 * Like a move, it is applied first and told to the server afterwards: a face that stays as it was
 * until a round trip completes reads as a click that missed. The optimistic row is announced rather
 * than handed back to one view, because the issue may be on screen more than once — a board behind a
 * dialog, My Issues in another tab of the same window — and every view already applies a change by
 * upserting on id. The server's own row is announced on top of it, which is what corrects `updatedAt`
 * and anything else the write moved; a refusal announces the row the issue started with.
 */
export async function assignIssue(issue: IssueSummary, member: TeamMemberDto | null): Promise<void> {
  const assignee = member && {
    id: member.userId,
    email: member.email,
    displayName: member.displayName,
    avatarUrl: member.avatarUrl,
    // The picker only ever offers current members of the team, so this is true of anyone it returns.
    isActive: true
  };

  if ((issue.assignee?.id ?? null) === (assignee?.id ?? null)) return;

  announce('Updated', { ...issue, assignee });

  try {
    announce('Updated', await issuesApi.update(issue.id, { assigneeId: assignee?.id ?? null }));
  } catch (failure) {
    announce('Updated', issue);
    toasts.error(failure instanceof ApiError ? failure.message : 'That assignment was refused.');
  }
}
