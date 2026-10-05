import type { IssueSummary, TeamMemberDto } from '$lib/api/types';
import { updateOptimistically } from '$lib/issues/changes';

/*
 * Handing an issue to someone, from wherever its assignee is drawn.
 *
 * A board row and a board card show the same person the same way, so reassigning from either means
 * the same thing, and the rule for what it writes lives here rather than once per view — the same
 * split `move.ts` makes for a drop. Like a move, it is applied first and told to the server
 * afterwards; see `updateOptimistically`.
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

  await updateOptimistically(
    issue,
    { ...issue, assignee },
    { assigneeId: assignee?.id ?? null },
    'That assignment was refused.'
  );
}
