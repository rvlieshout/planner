import { ApiError, issues as issuesApi } from '$lib/api';
import type { IssuePriority, IssueSummary } from '$lib/api/types';
import { announce } from '$lib/issues/changes';
import { toasts } from '$components/toast.svelte';

/*
 * Reprioritising an issue from wherever its priority is drawn — the counterpart of `assign.ts`, and
 * optimistic for the same reason: applied and announced first, corrected by the server's row, and
 * put back as it was if the write is refused.
 */
export async function prioritizeIssue(issue: IssueSummary, priority: IssuePriority): Promise<void> {
  if (issue.priority === priority) return;

  announce('Updated', { ...issue, priority });

  try {
    announce('Updated', await issuesApi.update(issue.id, { priority }));
  } catch (failure) {
    announce('Updated', issue);
    toasts.error(failure instanceof ApiError ? failure.message : 'That priority change was refused.');
  }
}
