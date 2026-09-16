import type { IconName } from '$lib/icons/icons';
import type {
  IssuePriority,
  IssueRelationType,
  MilestoneStatus,
  OrgRole,
  ProjectHealth,
  ProjectStatus,
  TeamRole,
  WorkflowStateType
} from '$lib/api/types';

/*
 * How each enum value is drawn: its icon, its wording, and the token its colour comes from.
 *
 * One table per enum, in the enum's own order, so a value added on the server is a missing row here
 * rather than a switch statement somewhere that silently falls through to a default.
 */

export interface Meta {
  label: string;
  icon: IconName;
  /** A CSS colour — a token reference wherever the meaning is one the theme already has a colour for. */
  color: string;
}

/**
 * Priority, drawn as Linear draws it: signal bars, with "no priority" the bars at rest rather than an
 * empty cell. An icon that means "none" is a value; a blank space is a question.
 */
export const PRIORITY: Record<IssuePriority, Meta> = {
  None: { label: 'No priority', icon: 'minus', color: 'var(--fg-tertiary)' },
  Urgent: { label: 'Urgent', icon: 'triangle-alert', color: 'var(--danger)' },
  High: { label: 'High', icon: 'signal-high', color: 'var(--warning)' },
  Medium: { label: 'Medium', icon: 'signal-medium', color: 'var(--fg-secondary)' },
  Low: { label: 'Low', icon: 'signal-low', color: 'var(--fg-tertiary)' }
};

/** Sort order for a priority picker and for grouping: Urgent first, None last. */
export const PRIORITY_ORDER: IssuePriority[] = ['Urgent', 'High', 'Medium', 'Low', 'None'];

/**
 * Workflow state *types*. A team may rename its columns freely, so anything that has to mean the same
 * thing across teams — My Issues groups, a rollup, "is this done?" — keys off the type, not the name.
 */
export const STATE_TYPE: Record<WorkflowStateType, Meta> = {
  Backlog: { label: 'Backlog', icon: 'circle-dashed', color: 'var(--fg-tertiary)' },
  Unstarted: { label: 'Todo', icon: 'circle', color: 'var(--fg-secondary)' },
  Started: { label: 'In Progress', icon: 'circle-dot', color: 'var(--warning)' },
  Completed: { label: 'Done', icon: 'square-check-big', color: 'var(--success)' },
  Canceled: { label: 'Canceled', icon: 'ban', color: 'var(--fg-tertiary)' }
};

/** The order My Issues groups in: what is being worked on first, what was abandoned last. */
export const STATE_TYPE_ORDER: WorkflowStateType[] = [
  'Started',
  'Unstarted',
  'Backlog',
  'Completed',
  'Canceled'
];

export const PROJECT_STATUS: Record<ProjectStatus, Meta> = {
  Backlog: { label: 'Backlog', icon: 'circle-dashed', color: 'var(--fg-tertiary)' },
  Planned: { label: 'Planned', icon: 'circle', color: 'var(--fg-secondary)' },
  InProgress: { label: 'In Progress', icon: 'circle-dot', color: 'var(--accent)' },
  Paused: { label: 'Paused', icon: 'clock', color: 'var(--warning)' },
  Completed: { label: 'Completed', icon: 'square-check-big', color: 'var(--success)' },
  Canceled: { label: 'Canceled', icon: 'ban', color: 'var(--fg-tertiary)' }
};

export const PROJECT_HEALTH: Record<ProjectHealth, Meta> = {
  OnTrack: { label: 'On track', icon: 'circle-check', color: 'var(--success)' },
  AtRisk: { label: 'At risk', icon: 'circle-alert', color: 'var(--warning)' },
  OffTrack: { label: 'Off track', icon: 'triangle-alert', color: 'var(--danger)' }
};

export const MILESTONE_STATUS: Record<MilestoneStatus, Meta> = {
  Upcoming: { label: 'Upcoming', icon: 'circle', color: 'var(--fg-secondary)' },
  Active: { label: 'Active', icon: 'circle-dot', color: 'var(--accent)' },
  Completed: { label: 'Completed', icon: 'square-check-big', color: 'var(--success)' }
};

export const TEAM_ROLE: Record<TeamRole, Meta & { hint: string }> = {
  Viewer: {
    label: 'Viewer',
    icon: 'user',
    color: 'var(--fg-tertiary)',
    hint: 'Reads everything in the team and can comment. Changes nothing.'
  },
  Member: {
    label: 'Member',
    icon: 'user',
    color: 'var(--fg-secondary)',
    hint: 'Creates and edits issues, projects, milestones and documents.'
  },
  Lead: {
    label: 'Lead',
    icon: 'shield',
    color: 'var(--accent)',
    hint: 'Everything a member can, plus team settings, membership, states and labels.'
  }
};

export const ORG_ROLE: Record<OrgRole, Meta & { hint: string }> = {
  owner: {
    label: 'Owner',
    icon: 'key-round',
    color: 'var(--accent)',
    hint: 'Everything an administrator can, plus granting and revoking ownership.'
  },
  admin: {
    label: 'Administrator',
    icon: 'shield',
    color: 'var(--accent)',
    hint: 'Manages users and teams, and administers every team.'
  },
  member: {
    label: 'Member',
    icon: 'user',
    color: 'var(--fg-secondary)',
    hint: 'Full author rights in the teams they belong to.'
  },
  guest: {
    label: 'Guest',
    icon: 'user',
    color: 'var(--fg-tertiary)',
    hint: 'Reads and comments in teams they were added to. Never creates content.'
  }
};

/** Relation wording, which inverts when the row was stored on the other issue. */
export const RELATION: Record<IssueRelationType, { outgoing: string; incoming: string }> = {
  Related: { outgoing: 'Related to', incoming: 'Related to' },
  Blocks: { outgoing: 'Blocks', incoming: 'Blocked by' },
  Duplicates: { outgoing: 'Duplicates', incoming: 'Duplicated by' }
};

export function relationLabel(type: IssueRelationType, isOutgoing: boolean): string {
  return isOutgoing ? RELATION[type].outgoing : RELATION[type].incoming;
}

/**
 * The estimate scale the picker offers. A value the server holds that is not on it is inserted on
 * load, so opening an issue estimated by some other means and saving it cannot round the number away.
 */
export const ESTIMATE_SCALE = [1, 2, 3, 5, 8, 13, 21];

/** Project and team colours: ten swatches as the quick answer, with a hex box as the honest one. */
export const SWATCHES = [
  '#5E6AD2',
  '#26A69A',
  '#3B82F6',
  '#8B5CF6',
  '#EC4899',
  '#EF4444',
  '#F59E0B',
  '#84CC16',
  '#14B8A6',
  '#64748B'
];

/** The activity feed's verbs, in the words a person would use for them. */
export const ACTIVITY_ACTION: Record<string, string> = {
  created: 'created this',
  updated: 'updated this',
  archived: 'archived this',
  restored: 'restored this',
  deleted: 'deleted this',
  state_changed: 'changed status',
  assignee_changed: 'changed the assignee',
  priority_changed: 'changed the priority',
  labels_changed: 'changed the labels',
  commented: 'commented',
  relation_added: 'added a relation',
  relation_removed: 'removed a relation',
  attachment_added: 'attached a file',
  member_added: 'added a member',
  member_removed: 'removed a member',
  member_role_changed: 'changed a member role'
};
