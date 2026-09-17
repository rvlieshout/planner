import type { IssueSummary, WorkflowStateDto } from '$lib/api/types';

/*
 * How a board is laid out.
 *
 * Columns go one per lane — one column wide, one column deep — with a single exception: **Todo sits
 * on top of Backlog in one lane**. What a board is asked to do most often is promote something out of
 * the backlog, and stacking the two makes that a drag straight up into the column above rather than a
 * hunt for one somewhere off to the right. The arrangement says where the work is going before the
 * drag starts.
 *
 * The two stay two columns while they do it — their own headers, their own counts, their own drop
 * targets — because they are still two workflow states and a drop has to land in one of them.
 *
 * The pairing is by state *type* rather than by name: a team may rename its states but cannot change
 * what they mean. It covers every state of those two types rather than only the two a team starts
 * with, and the lane takes the leftmost of the positions those states hold, so the rest of the board
 * keeps the order the team gave it. A team with only one of the two types gets an ordinary
 * single-column lane, which is exactly what it had before.
 */

export interface BoardColumn {
  state: WorkflowStateDto;
  issues: IssueSummary[];
}

export interface BoardLane {
  key: string;
  /** One column, or Todo above Backlog. Never more. */
  columns: BoardColumn[];
}

/** One state's issues, in the order every view shows them: the rank the board drags them into. */
export function issuesIn(issues: IssueSummary[], stateId: string): IssueSummary[] {
  return issues
    .filter((issue) => issue.stateId === stateId)
    .sort((a, b) => a.sortOrder - b.sortOrder || a.number - b.number);
}

export function layOut(states: WorkflowStateDto[], issues: IssueSummary[]): BoardLane[] {
  const ordered = [...states].sort((a, b) => a.position - b.position);

  const columnsOf = (state: WorkflowStateDto): BoardColumn => ({
    state,
    issues: issuesIn(issues, state.id)
  });

  const unstarted = ordered.filter((state) => state.type === 'Unstarted');
  const backlog = ordered.filter((state) => state.type === 'Backlog');
  const paired = unstarted.length > 0 && backlog.length > 0;

  if (!paired) {
    return ordered.map((state) => ({ key: state.id, columns: [columnsOf(state)] }));
  }

  // The lane stands where the leftmost of the paired states stood.
  const anchor = Math.min(...[...unstarted, ...backlog].map((state) => ordered.indexOf(state)));
  const lanes: BoardLane[] = [];

  for (const [index, state] of ordered.entries()) {
    if (state.type === 'Unstarted' || state.type === 'Backlog') {
      if (index !== anchor) continue;

      lanes.push({
        key: 'todo-backlog',
        // Todo on top, Backlog underneath, each keeping its own order within its type.
        columns: [...unstarted.map(columnsOf), ...backlog.map(columnsOf)]
      });
      continue;
    }

    lanes.push({ key: state.id, columns: [columnsOf(state)] });
  }

  return lanes;
}

/**
 * "11 issues in 6 columns" — the line the status bar shows for a board.
 *
 * The same states are groups rather than columns once the list view is showing them, and the status
 * bar describes what is on screen, so the noun follows the view.
 */
export function boardSummary(lanes: BoardLane[], noun: 'column' | 'group' = 'column'): string {
  const columns = lanes.flatMap((lane) => lane.columns);
  const total = columns.reduce((sum, column) => sum + column.issues.length, 0);

  return `${total} ${total === 1 ? 'issue' : 'issues'} in ${columns.length} ${
    columns.length === 1 ? noun : `${noun}s`
  }`;
}
