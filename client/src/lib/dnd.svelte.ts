import type { IssueSummary } from '$lib/api/types';

/*
 * Dragging issues, between board columns and between My Issues groups.
 *
 * Built on pointer events rather than HTML5 drag-and-drop. The native API owns the drag image, and
 * what it gives you is a washed-out screenshot of the element with no say in its size, its opacity or
 * whether it is the right thing to be carrying at all; it also cannot be styled, cannot be moved, and
 * behaves differently in every browser. A pointer drag draws its own card, which is the only way the
 * four answers below can be given at once.
 *
 * A drop is a guess until the board answers three questions, so it answers all of them:
 *
 *   What am I carrying?      the card under the cursor          (DragGhost)
 *   Where would it land?     a rule between two rows            (indicator)
 *   Would this take it?      the column tinted and outlined     (dropKey)
 *   Which one is in flight?  the original row faded             (isDragging)
 *
 * Targets are found by hit-testing the DOM rather than by registering themselves: a column marks
 * itself `data-drop-key` and its rows `data-issue-id`, and that is the whole contract. One walk of
 * the realised rows produces both the index and the offset the rule is drawn at, so what is shown can
 * never point at a different gap from the one that is used.
 */

/** How far the pointer travels before a press becomes a drag rather than a click. */
const THRESHOLD = 4;

export interface DropTarget {
  key: string;
  /** Where the issue would land among the target's rows, or null for a target that has no order. */
  index: number | null;
  /** Distance from the top of the scrolling list to draw the rule at. Null when there is none. */
  offset: number | null;
}

class Drag {
  issue = $state<IssueSummary | null>(null);
  x = $state(0);
  y = $state(0);

  /** The column or group under the cursor, if it will take this issue. */
  target = $state<DropTarget | null>(null);

  #origin: { x: number; y: number } | null = null;
  #candidate: IssueSummary | null = null;
  #ondrop: ((target: DropTarget) => void | Promise<void>) | null = null;
  #accepts: ((key: string) => boolean) | null = null;
  #ordered = true;

  get active(): boolean {
    return this.issue !== null;
  }

  isDragging(issueId: string): boolean {
    return this.issue?.id === issueId;
  }

  isTarget(key: string): boolean {
    return this.target?.key === key;
  }

  /**
   * Remembers a press without handling it, so a click still selects and a double-click still opens.
   * The drag only begins once the pointer has travelled far enough to mean one.
   */
  press(
    event: PointerEvent,
    issue: IssueSummary,
    options: {
      ondrop: (target: DropTarget) => void | Promise<void>;
      /** Whether a given drop key would take this issue. Defaults to every key but its own. */
      accepts?: (key: string) => boolean;
      /** False for My Issues, whose groups set a state and do not choose a position. */
      ordered?: boolean;
    }
  ): void {
    // Only the primary button, and never from inside a control the row happens to contain.
    if (event.button !== 0) return;

    this.#origin = { x: event.clientX, y: event.clientY };
    this.#candidate = issue;
    this.#ondrop = options.ondrop;
    this.#accepts = options.accepts ?? null;
    this.#ordered = options.ordered ?? true;

    window.addEventListener('pointermove', this.#onMove);
    window.addEventListener('pointerup', this.#onUp);
    window.addEventListener('pointercancel', this.#cancel);
  }

  #onMove = (event: PointerEvent) => {
    if (!this.#origin || !this.#candidate) return;

    if (!this.issue) {
      const travelled = Math.hypot(event.clientX - this.#origin.x, event.clientY - this.#origin.y);
      if (travelled < THRESHOLD) return;

      this.issue = this.#candidate;
      document.body.style.userSelect = 'none';
      document.body.style.cursor = 'grabbing';
    }

    this.x = event.clientX;
    this.y = event.clientY;
    this.target = this.#hitTest(event.clientX, event.clientY);
  };

  #onUp = async () => {
    const target = this.target;
    const ondrop = this.#ondrop;
    const dragged = this.issue;

    this.#reset();

    if (dragged && target && ondrop) await ondrop(target);
  };

  #cancel = () => this.#reset();

  #reset(): void {
    window.removeEventListener('pointermove', this.#onMove);
    window.removeEventListener('pointerup', this.#onUp);
    window.removeEventListener('pointercancel', this.#cancel);

    document.body.style.userSelect = '';
    document.body.style.cursor = '';

    this.issue = null;
    this.target = null;
    this.#origin = null;
    this.#candidate = null;
    this.#ondrop = null;
    this.#accepts = null;
  }

  /**
   * Which column the cursor is over, and where in it.
   *
   * `elementsFromPoint` rather than the event's own target, because the ghost sits under the cursor
   * and would otherwise be the only thing ever hit.
   */
  #hitTest(x: number, y: number): DropTarget | null {
    const container = document
      .elementsFromPoint(x, y)
      .find((element) => element instanceof HTMLElement && element.closest('[data-drop-key]'))
      ?.closest('[data-drop-key]') as HTMLElement | undefined;

    if (!container) return null;

    const key = container.dataset.dropKey!;

    // Hovering the group an issue is already in clears the highlight rather than leaving the last one
    // lit, so "this would do nothing" is visible as the absence of a target.
    if (this.#accepts && !this.#accepts(key)) return null;

    if (!this.#ordered) return { key, index: null, offset: null };

    const rows = [...container.querySelectorAll<HTMLElement>('[data-issue-id]')];
    const bounds = container.getBoundingClientRect();

    let index = rows.length;
    let offset = container.scrollHeight;

    for (const [position, row] of rows.entries()) {
      const rect = row.getBoundingClientRect();

      if (y < rect.top + rect.height / 2) {
        index = position;
        offset = rect.top - bounds.top + container.scrollTop;
        break;
      }
    }

    if (index === rows.length && rows.length > 0) {
      const last = rows[rows.length - 1].getBoundingClientRect();
      offset = last.bottom - bounds.top + container.scrollTop;
    } else if (rows.length === 0) {
      offset = 0;
    }

    return { key, index, offset };
  }
}

export const drag = new Drag();

/**
 * The two rows a drop landed between, as the anchors `POST /issues/{id}/move` takes.
 *
 * The server writes a rank key between theirs, so a reorder is one row updated rather than a column
 * renumbered. The issue being moved is skipped: dropping a card one place below itself must land
 * between its *other* neighbours, or the move resolves to where it already was.
 */
export function moveAnchors(
  column: IssueSummary[],
  index: number,
  movingId: string
): { afterIssueId: string | null; beforeIssueId: string | null } {
  // The index was measured against the rows actually on screen, which include the one being dragged
  // when it started in this column. So the neighbours are found by walking outwards from the gap and
  // stepping over that row — dropping a card one place below itself has to land between its *other*
  // two neighbours, or the move resolves to exactly where it already was.
  let above = Math.min(index, column.length) - 1;
  while (above >= 0 && column[above].id === movingId) above -= 1;

  let below = Math.max(index, 0);
  while (below < column.length && column[below].id === movingId) below += 1;

  return {
    afterIssueId: column[above]?.id ?? null,
    beforeIssueId: column[below]?.id ?? null
  };
}
