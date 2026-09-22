/*
 * TypeScript mirror of Planner.Contracts.
 *
 * The desktop client references that project directly, so the compiler checks both ends of the wire
 * for it. A browser cannot do that, so this file stands in its place: one module, ordered to match the
 * C# files it mirrors, so a contract change is a diff in the same shape here.
 *
 * Three server conventions the shapes below depend on:
 *   - Ids are base58, not hyphenated uuids. Still strings here, and still opaque, so nothing below
 *     changes shape — but a hard-coded uuid in a test fixture or a URL will no longer match.
 *   - Enums travel as names. "Urgent", never 1 — over REST and over SignalR alike.
 *   - PATCH is a true patch. An absent key is untouched, an explicit null clears the field. That is
 *     what Optional<T> means on the server, and in TypeScript it falls out of `?:` plus `| null`.
 */

/**
 * A GUID, as the server writes it: base58, 22 characters, e.g. `1CFM9HDkWavHzjEZuAP3qG` — not the
 * hyphenated form. Opaque either way: compare one, round-trip one, never build one. Aliased so intent
 * survives in a field list of strings.
 */
export type Guid = string;

/** ISO 8601 with an offset: `2026-08-27T15:44:45+00:00`. */
export type Timestamp = string;

/** A calendar date with no time: `2026-09-30`. DateOnly on the server. */
export type DateOnlyString = string;

/* ------------------------------------------------------------------- enums ---- */

export const TEAM_ROLES = ['Viewer', 'Member', 'Lead'] as const;
export type TeamRole = (typeof TEAM_ROLES)[number];

export const WORKFLOW_STATE_TYPES = [
  'Backlog',
  'Unstarted',
  'Started',
  'Completed',
  'Canceled'
] as const;
export type WorkflowStateType = (typeof WORKFLOW_STATE_TYPES)[number];

/** Linear-style ordering: Urgent sorts first, None sorts last. */
export const ISSUE_PRIORITIES = ['None', 'Urgent', 'High', 'Medium', 'Low'] as const;
export type IssuePriority = (typeof ISSUE_PRIORITIES)[number];

export const PROJECT_STATUSES = [
  'Backlog',
  'Planned',
  'InProgress',
  'Paused',
  'Completed',
  'Canceled'
] as const;
export type ProjectStatus = (typeof PROJECT_STATUSES)[number];

export const PROJECT_HEALTHS = ['OnTrack', 'AtRisk', 'OffTrack'] as const;
export type ProjectHealth = (typeof PROJECT_HEALTHS)[number];

export const MILESTONE_STATUSES = ['Upcoming', 'Active', 'Completed'] as const;
export type MilestoneStatus = (typeof MILESTONE_STATUSES)[number];

export const ISSUE_RELATION_TYPES = ['Related', 'Blocks', 'Duplicates'] as const;
export type IssueRelationType = (typeof ISSUE_RELATION_TYPES)[number];

/** Organisation roles are Identity roles, not an enum, and travel lower-cased. */
export const ORG_ROLES = ['owner', 'admin', 'member', 'guest'] as const;
export type OrgRole = (typeof ORG_ROLES)[number];

/* ------------------------------------------------------------------ common ---- */

export interface PagedResult<T> {
  items: T[];
  page: number;
  pageSize: number;
  totalCount: number;
  totalPages: number;
  hasNext: boolean;
}

/* -------------------------------------------------------------------- auth ---- */

export interface TokenResponse {
  access_token: string;
  refresh_token?: string;
  token_type: string;
  expires_in: number;
  scope?: string;
}

export interface MeTeamMembership {
  teamId: Guid;
  teamKey: string;
  teamName: string;
  role: TeamRole;
}

export interface MeResponse {
  id: Guid;
  email: string;
  displayName: string;
  avatarUrl: string | null;
  timeZone: string;
  role: OrgRole;
  isActive: boolean;
  teams: MeTeamMembership[];
}

export interface UserSummary {
  id: Guid;
  email: string;
  displayName: string;
  avatarUrl: string | null;
  isActive: boolean;
}

export interface UserDetail extends UserSummary {
  timeZone: string;
  role: OrgRole;
  createdAt: Timestamp;
  lastSeenAt: Timestamp | null;
}

export interface CreateUserRequest {
  email: string;
  password: string;
  displayName: string;
  role?: OrgRole;
  timeZone?: string;
}

export interface UpdateProfileRequest {
  displayName?: string;
  avatarUrl?: string | null;
  timeZone?: string;
}

export interface UpdateUserRequest {
  displayName?: string;
  avatarUrl?: string | null;
  timeZone?: string;
  isActive?: boolean;
  role?: OrgRole;
}

export interface ChangePasswordRequest {
  currentPassword: string;
  newPassword: string;
}

/* ------------------------------------------------------------------- teams ---- */

export interface TeamDto {
  id: Guid;
  key: string;
  name: string;
  description: string | null;
  color: string;
  isPrivate: boolean;
  memberCount: number;
  createdAt: Timestamp;
  updatedAt: Timestamp;
  archivedAt: Timestamp | null;
}

export interface CreateTeamRequest {
  key: string;
  name: string;
  description?: string | null;
  color?: string;
  isPrivate?: boolean;
}

export interface UpdateTeamRequest {
  name?: string;
  description?: string | null;
  color?: string;
  isPrivate?: boolean;
}

export interface TeamMemberDto {
  teamId: Guid;
  userId: Guid;
  displayName: string;
  email: string;
  avatarUrl: string | null;
  role: TeamRole;
  createdAt: Timestamp;
}

export interface WorkflowStateDto {
  id: Guid;
  teamId: Guid;
  name: string;
  type: WorkflowStateType;
  color: string;
  rank: string;
  isDefault: boolean;
}

export interface CreateWorkflowStateRequest {
  name: string;
  type: WorkflowStateType;
  color?: string;
  rank?: string | null;
  isDefault?: boolean;
}

export interface UpdateWorkflowStateRequest {
  name?: string;
  type?: WorkflowStateType;
  color?: string;
  rank?: string;
  isDefault?: boolean;
}

export interface LabelDto {
  id: Guid;
  teamId: Guid | null;
  name: string;
  color: string;
  description: string | null;
}

export interface CreateLabelRequest {
  name: string;
  color?: string;
  description?: string | null;
}

export interface UpdateLabelRequest {
  name?: string;
  color?: string;
  description?: string | null;
}

/* --------------------------------------------------- projects & milestones ---- */

/** Issue rollup, counted from workflow-state types at read time. */
export interface ProjectProgress {
  total: number;
  completed: number;
  started: number;
  canceled: number;
  /** Completed over non-canceled work, 0..1. */
  ratio: number;
}

export interface ProjectDto {
  id: Guid;
  teamId: Guid;
  teamKey: string;
  name: string;
  summary: string | null;
  description: string | null;
  status: ProjectStatus;
  health: ProjectHealth;
  color: string;
  lead: UserSummary | null;
  startDate: DateOnlyString | null;
  targetDate: DateOnlyString | null;
  rank: string;
  progress: ProjectProgress;
  createdAt: Timestamp;
  updatedAt: Timestamp;
  completedAt: Timestamp | null;
  archivedAt: Timestamp | null;
}

export interface CreateProjectRequest {
  teamId: Guid;
  name: string;
  summary?: string | null;
  description?: string | null;
  status?: ProjectStatus;
  health?: ProjectHealth;
  color?: string;
  leadUserId?: Guid | null;
  startDate?: DateOnlyString | null;
  targetDate?: DateOnlyString | null;
}

export interface UpdateProjectRequest {
  name?: string;
  summary?: string | null;
  description?: string | null;
  status?: ProjectStatus;
  health?: ProjectHealth;
  color?: string;
  leadUserId?: Guid | null;
  startDate?: DateOnlyString | null;
  targetDate?: DateOnlyString | null;
  rank?: string;
}

export interface MilestoneDto {
  id: Guid;
  projectId: Guid;
  name: string;
  description: string | null;
  targetDate: DateOnlyString | null;
  status: MilestoneStatus;
  rank: string;
  progress: ProjectProgress;
  createdAt: Timestamp;
  updatedAt: Timestamp;
  completedAt: Timestamp | null;
}

export interface CreateMilestoneRequest {
  name: string;
  description?: string | null;
  targetDate?: DateOnlyString | null;
  status?: MilestoneStatus;
}

export interface UpdateMilestoneRequest {
  name?: string;
  description?: string | null;
  targetDate?: DateOnlyString | null;
  status?: MilestoneStatus;
  rank?: string;
}

/* --------------------------------------------------------------- documents ---- */

export interface DocumentSummary {
  id: Guid;
  teamId: Guid;
  projectId: Guid | null;
  title: string;
  createdBy: UserSummary;
  updatedBy: UserSummary | null;
  createdAt: Timestamp;
  updatedAt: Timestamp;
  archivedAt: Timestamp | null;
}

export interface DocumentDto extends DocumentSummary {
  content: string;
}

export interface CreateDocumentRequest {
  teamId: Guid;
  projectId?: Guid | null;
  title: string;
  content?: string;
}

export interface UpdateDocumentRequest {
  title?: string;
  content?: string;
  projectId?: Guid | null;
}

/* ------------------------------------------------------------------ issues ---- */

/** Board and list row. Deliberately flat and small — a board fetches hundreds of these. */
export interface IssueSummary {
  id: Guid;
  key: string;
  teamId: Guid;
  number: number;
  title: string;
  stateId: Guid;
  stateName: string;
  stateType: WorkflowStateType;
  stateColor: string;
  priority: IssuePriority;
  assignee: UserSummary | null;
  projectId: Guid | null;
  milestoneId: Guid | null;
  parentId: Guid | null;
  estimate: number | null;
  dueDate: DateOnlyString | null;
  rank: string;
  labels: LabelDto[];
  subIssueCount: number;
  commentCount: number;
  createdAt: Timestamp;
  updatedAt: Timestamp;
  completedAt: Timestamp | null;
  archivedAt: Timestamp | null;
}

export interface IssueDetail {
  id: Guid;
  key: string;
  teamId: Guid;
  number: number;
  title: string;
  description: string | null;
  stateId: Guid;
  stateName: string;
  stateType: WorkflowStateType;
  stateColor: string;
  priority: IssuePriority;
  assignee: UserSummary | null;
  creator: UserSummary;
  projectId: Guid | null;
  projectName: string | null;
  milestoneId: Guid | null;
  milestoneName: string | null;
  parentId: Guid | null;
  parentKey: string | null;
  estimate: number | null;
  dueDate: DateOnlyString | null;
  rank: string;
  labels: LabelDto[];
  children: IssueSummary[];
  relations: IssueRelationDto[];
  attachments: AttachmentDto[];
  commentCount: number;
  createdAt: Timestamp;
  updatedAt: Timestamp;
  startedAt: Timestamp | null;
  completedAt: Timestamp | null;
  canceledAt: Timestamp | null;
  archivedAt: Timestamp | null;
}

export interface CreateIssueRequest {
  teamId: Guid;
  title: string;
  description?: string | null;
  stateId?: Guid | null;
  priority?: IssuePriority;
  assigneeId?: Guid | null;
  projectId?: Guid | null;
  milestoneId?: Guid | null;
  parentId?: Guid | null;
  estimate?: number | null;
  dueDate?: DateOnlyString | null;
  labelIds?: Guid[];
}

export interface UpdateIssueRequest {
  title?: string;
  description?: string | null;
  stateId?: Guid;
  priority?: IssuePriority;
  assigneeId?: Guid | null;
  projectId?: Guid | null;
  milestoneId?: Guid | null;
  parentId?: Guid | null;
  estimate?: number | null;
  dueDate?: DateOnlyString | null;
  rank?: string;
  labelIds?: Guid[];
}

/** Board drag-and-drop: change column and rank in one atomic call. */
export interface MoveIssueRequest {
  stateId?: Guid | null;
  rank?: string | null;
  afterIssueId?: Guid | null;
  beforeIssueId?: Guid | null;
}

/**
 * Issue list filter. Array fields become repeated query parameters, which the API reads as OR sets —
 * except `labelId`, which it AND-combines, because that is what a label filter on a board means.
 */
export interface IssueFilter {
  teamId?: Guid;
  projectId?: Guid;
  milestoneId?: Guid;
  parentId?: Guid;
  stateId?: Guid[];
  stateType?: WorkflowStateType[];
  assigneeId?: Guid[];
  labelId?: Guid[];
  priority?: IssuePriority[];
  unassigned?: boolean;
  includeArchived?: boolean;
  topLevelOnly?: boolean;
  search?: string;
  dueBefore?: DateOnlyString;
  dueAfter?: DateOnlyString;
  updatedSince?: Timestamp;
  sort?: IssueSort;
  page?: number;
  pageSize?: number;
}

/** The only values the server accepts; anything else silently falls back to `-updatedAt`. */
export type IssueSort =
  | '-updatedAt'
  | 'updatedAt'
  | 'board'
  | 'priority'
  | 'dueDate'
  | 'createdAt'
  | '-createdAt'
  | 'number'
  | 'rank';

export interface IssueRelationDto {
  id: Guid;
  type: IssueRelationType;
  /** False when the row was stored on the other issue and is shown inverted here. */
  isOutgoing: boolean;
  issueId: Guid;
  issueKey: string;
  issueTitle: string;
  stateType: WorkflowStateType;
}

export interface CreateIssueRelationRequest {
  targetIssueId: Guid;
  type: IssueRelationType;
}

export interface CommentDto {
  id: Guid;
  issueId: Guid;
  author: UserSummary;
  body: string;
  parentCommentId: Guid | null;
  createdAt: Timestamp;
  updatedAt: Timestamp;
  editedAt: Timestamp | null;
}

export interface AttachmentDto {
  id: Guid;
  issueId: Guid;
  fileName: string;
  contentType: string | null;
  sizeBytes: number | null;
  storageUri: string;
  uploadedBy: UserSummary;
  createdAt: Timestamp;
}

export interface CreateAttachmentRequest {
  fileName: string;
  storageUri: string;
  contentType?: string | null;
  sizeBytes?: number | null;
}

export interface ActivityEventDto {
  id: Guid;
  entityType: string;
  entityId: Guid;
  teamId: Guid | null;
  projectId: Guid | null;
  issueId: Guid | null;
  actor: UserSummary;
  action: string;
  data: Record<string, unknown> | null;
  createdAt: Timestamp;
}

/* ---------------------------------------------------------------- realtime ---- */

export type ChangeKind = 'Created' | 'Updated' | 'Deleted' | 'Archived' | 'Restored';

/** Envelope pushed over SignalR for every mutation. `entity` is null for a delete. */
export interface EntityChange<T> {
  kind: ChangeKind;
  entityType: string;
  id: Guid;
  teamId: Guid | null;
  projectId: Guid | null;
  issueId: Guid | null;
  actorId: Guid;
  occurredAt: Timestamp;
  entity: T | null;
}
