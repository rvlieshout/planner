import type { Guid, IssueSummary, ProjectProgress } from '$lib/api/types';

/*
 * Project and milestone rollups, counted from the issues a page is already holding.
 *
 * The server computes the same numbers on read — see `ProjectProjection` and `MilestoneProjection`
 * in Planner.Api/Common/Mapping.cs — so a freshly loaded page agrees with it exactly. It also
 * republishes a project and a milestone whenever an issue write moves their counts, which is how the
 * sidebar and the milestone rows in project settings stay current: neither holds an issue list of
 * its own.
 *
 * A view that *does* hold the issues counts them itself instead, for two reasons. It needs no round
 * trip, so an issue saved locally moves the rollup at the same moment it moves the board. And the
 * rollup then cannot disagree with the issues rendered beside it from the same array, which is the
 * failure these replaced: a board that had already grown a card, above a bar that had not.
 *
 * Counting scope mirrors the server's: every non-archived issue, sub-issues included. A page whose
 * issue list is filtered to less than that must not use these.
 */

/** A project or milestone with nothing in it. The rollup a milestone with no issues shows. */
export const EMPTY_PROGRESS: ProjectProgress = {
  total: 0,
  completed: 0,
  started: 0,
  canceled: 0,
  ratio: 0
};

export function rollUp(issues: Iterable<IssueSummary>): ProjectProgress {
  let total = 0;
  let completed = 0;
  let started = 0;
  let canceled = 0;

  for (const issue of issues) {
    // An archived issue leaves the list on its own `Archived` change, but an `Updated` echo can
    // still carry one, and the server counts neither.
    if (issue.archivedAt) continue;

    total++;

    if (issue.stateType === 'Completed') completed++;
    else if (issue.stateType === 'Started') started++;
    else if (issue.stateType === 'Canceled') canceled++;
  }

  const scope = total - canceled;

  return {
    total,
    completed,
    started,
    canceled,
    // Completed over non-canceled work, to the same four decimals the server rounds to.
    ratio: scope <= 0 ? 0 : Math.round((completed / scope) * 10_000) / 10_000
  };
}

/** One rollup per milestone. Issues with no milestone are counted in the project's, not here. */
export function rollUpByMilestone(issues: Iterable<IssueSummary>): Map<Guid, ProjectProgress> {
  const grouped = new Map<Guid, IssueSummary[]>();

  for (const issue of issues) {
    if (!issue.milestoneId) continue;

    const bucket = grouped.get(issue.milestoneId);
    if (bucket) bucket.push(issue);
    else grouped.set(issue.milestoneId, [issue]);
  }

  return new Map([...grouped].map(([id, bucket]) => [id, rollUp(bucket)]));
}
