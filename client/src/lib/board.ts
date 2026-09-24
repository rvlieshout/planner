import type { IssueSummary, WorkflowStateDto } from '$lib/api/types';
import { compareRank } from '$lib/rank';

/*
 * How a board is laid out: one column per workflow state, in the order the team ranked them. Every
 * state is treated alike, whatever its type, so a team that splits its work into more or fewer
 * columns sees exactly the sequence it set up.
 */

export interface BoardColumn {
  state: WorkflowStateDto;
  issues: IssueSummary[];
}

/** One state's issues, in the order every view shows them: the rank the board drags them into. */
export function issuesIn(issues: IssueSummary[], stateId: string): IssueSummary[] {
  return issues
    .filter((issue) => issue.stateId === stateId)
    .sort((a, b) => compareRank(a.rank, b.rank) || a.number - b.number);
}

export function layOut(states: WorkflowStateDto[], issues: IssueSummary[]): BoardColumn[] {
  return [...states]
    .sort((a, b) => compareRank(a.rank, b.rank) || a.name.localeCompare(b.name))
    .map((state) => ({ state, issues: issuesIn(issues, state.id) }));
}

/**
 * "11 issues in 6 columns" — the line the status bar shows for a board.
 *
 * The same states are groups rather than columns once the list view is showing them, and the status
 * bar describes what is on screen, so the noun follows the view.
 */
export function boardSummary(columns: BoardColumn[], noun: 'column' | 'group' = 'column'): string {
  const total = columns.reduce((sum, column) => sum + column.issues.length, 0);

  return `${total} ${total === 1 ? 'issue' : 'issues'} in ${columns.length} ${
    columns.length === 1 ? noun : `${noun}s`
  }`;
}
