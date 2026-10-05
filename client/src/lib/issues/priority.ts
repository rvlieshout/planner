import type { IssuePriority, IssueSummary } from '$lib/api/types';
import { updateOptimistically } from '$lib/issues/changes';

/*
 * Reprioritising an issue from wherever its priority is drawn — the counterpart of `assign.ts`, and
 * optimistic for the same reason.
 */
export async function prioritizeIssue(issue: IssueSummary, priority: IssuePriority): Promise<void> {
  if (issue.priority === priority) return;

  await updateOptimistically(issue, { ...issue, priority }, { priority }, 'That priority change was refused.');
}
