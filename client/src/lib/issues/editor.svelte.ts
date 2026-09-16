import type { Guid } from '$lib/api/types';

/*
 * Which issue form is open, if any.
 *
 * The form is a real modal dialog and can be opened from anywhere — a toolbar, a board card, a
 * keyboard shortcut, "create sub-issue" on a detail page — so what is open lives here rather than in
 * whichever view happened to ask. The dialog itself is mounted once, by the application shell.
 */

export interface IssueEditorTarget {
  /** Editing an existing issue. Omitted for a new one. */
  issueId?: Guid;
  teamId?: Guid;
  /** Pre-selected when the form is opened from inside a project, or from a parent issue. */
  projectId?: Guid | null;
  milestoneId?: Guid | null;
  parentId?: Guid | null;
}

class IssueEditor {
  target = $state<IssueEditorTarget | null>(null);

  get isOpen(): boolean {
    return this.target !== null;
  }

  /** Only the title is required, so an issue can be captured in two keystrokes and filled in later. */
  create(target: Omit<IssueEditorTarget, 'issueId'> = {}): void {
    this.target = { ...target };
  }

  edit(issueId: Guid): void {
    this.target = { issueId };
  }

  close(): void {
    this.target = null;
  }
}

export const issueEditor = new IssueEditor();
