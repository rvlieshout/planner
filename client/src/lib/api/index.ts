import { request, requestBlob, requestRaw, type QueryParams } from './http';
import type {
  ActivityEventDto,
  AttachmentDto,
  ChangePasswordRequest,
  CommentDto,
  CreateAttachmentRequest,
  CreateDocumentRequest,
  CreateIssueRelationRequest,
  CreateIssueRequest,
  CreateLabelRequest,
  CreateMilestoneRequest,
  CreateProjectRequest,
  CreateTeamRequest,
  CreateUserRequest,
  CreateWorkflowStateRequest,
  DocumentDto,
  DocumentSummary,
  Guid,
  IssueDetail,
  IssueFilter,
  IssueRelationDto,
  IssueSummary,
  LabelDto,
  MeResponse,
  MilestoneDto,
  MoveIssueRequest,
  PagedResult,
  ProjectDto,
  TeamDto,
  TeamMemberDto,
  TeamRole,
  UpdateDocumentRequest,
  UpdateIssueRequest,
  UpdateLabelRequest,
  UpdateMilestoneRequest,
  UpdateProfileRequest,
  UpdateProjectRequest,
  UpdateTeamRequest,
  UpdateUserRequest,
  UpdateWorkflowStateRequest,
  UserDetail,
  UserSummary,
  WorkflowStateDto
} from './types';

export { ApiError } from './errors';
export type { ProblemDetails } from './errors';
export { setUnauthorizedHandler } from './http';

/** The API's own ceiling. Asking for more than this makes it fall back to 50. */
export const MAX_PAGE_SIZE = 200;

const v1 = '/api/v1';

type Signal = { signal?: AbortSignal };

/* -------------------------------------------------------------------- me ---- */

export const me = {
  get: (o: Signal = {}) => request<MeResponse>(`${v1}/me`, o),

  update: (body: UpdateProfileRequest, o: Signal = {}) =>
    request<MeResponse>(`${v1}/me`, { ...o, method: 'PATCH', body }),

  changePassword: (body: ChangePasswordRequest, o: Signal = {}) =>
    request<void>(`${v1}/me/password`, { ...o, method: 'POST', body })
};

/* ----------------------------------------------------------------- users ---- */

export const users = {
  list: (
    params: { search?: string; includeInactive?: boolean; page?: number; pageSize?: number } = {},
    o: Signal = {}
  ) => request<PagedResult<UserSummary>>(`${v1}/users`, { ...o, query: params }),

  get: (id: Guid, o: Signal = {}) => request<UserDetail>(`${v1}/users/${id}`, o),

  create: (body: CreateUserRequest, o: Signal = {}) =>
    request<UserSummary>(`${v1}/users`, { ...o, method: 'POST', body }),

  update: (id: Guid, body: UpdateUserRequest, o: Signal = {}) =>
    request<UserSummary>(`${v1}/users/${id}`, { ...o, method: 'PATCH', body }),

  resetPassword: (id: Guid, newPassword: string, o: Signal = {}) =>
    request<void>(`${v1}/users/${id}/password`, { ...o, method: 'POST', body: { newPassword } }),

  deactivate: (id: Guid, o: Signal = {}) =>
    request<void>(`${v1}/users/${id}`, { ...o, method: 'DELETE' })
};

/* ----------------------------------------------------------------- teams ---- */

export const teams = {
  list: (includeArchived = false, o: Signal = {}) =>
    request<TeamDto[]>(`${v1}/teams`, { ...o, query: { includeArchived } }),

  get: (id: Guid, o: Signal = {}) => request<TeamDto>(`${v1}/teams/${id}`, o),

  create: (body: CreateTeamRequest, o: Signal = {}) =>
    request<TeamDto>(`${v1}/teams`, { ...o, method: 'POST', body }),

  update: (id: Guid, body: UpdateTeamRequest, o: Signal = {}) =>
    request<TeamDto>(`${v1}/teams/${id}`, { ...o, method: 'PATCH', body }),

  archive: (id: Guid, o: Signal = {}) =>
    request<TeamDto>(`${v1}/teams/${id}/archive`, { ...o, method: 'POST' }),

  restore: (id: Guid, o: Signal = {}) =>
    request<TeamDto>(`${v1}/teams/${id}/restore`, { ...o, method: 'POST' }),

  members: (id: Guid, o: Signal = {}) => request<TeamMemberDto[]>(`${v1}/teams/${id}/members`, o),

  addMember: (id: Guid, userId: Guid, role: TeamRole, o: Signal = {}) =>
    request<TeamMemberDto>(`${v1}/teams/${id}/members`, {
      ...o,
      method: 'POST',
      body: { userId, role }
    }),

  updateMember: (id: Guid, userId: Guid, role: TeamRole, o: Signal = {}) =>
    request<TeamMemberDto>(`${v1}/teams/${id}/members/${userId}`, {
      ...o,
      method: 'PATCH',
      body: { role }
    }),

  removeMember: (id: Guid, userId: Guid, o: Signal = {}) =>
    request<void>(`${v1}/teams/${id}/members/${userId}`, { ...o, method: 'DELETE' }),

  states: (id: Guid, o: Signal = {}) => request<WorkflowStateDto[]>(`${v1}/teams/${id}/states`, o),

  createState: (id: Guid, body: CreateWorkflowStateRequest, o: Signal = {}) =>
    request<WorkflowStateDto>(`${v1}/teams/${id}/states`, { ...o, method: 'POST', body }),

  updateState: (id: Guid, stateId: Guid, body: UpdateWorkflowStateRequest, o: Signal = {}) =>
    request<WorkflowStateDto>(`${v1}/teams/${id}/states/${stateId}`, {
      ...o,
      method: 'PATCH',
      body
    }),

  deleteState: (id: Guid, stateId: Guid, o: Signal = {}) =>
    request<void>(`${v1}/teams/${id}/states/${stateId}`, { ...o, method: 'DELETE' }),

  labels: (id: Guid, o: Signal = {}) => request<LabelDto[]>(`${v1}/teams/${id}/labels`, o),

  createLabel: (id: Guid, body: CreateLabelRequest, o: Signal = {}) =>
    request<LabelDto>(`${v1}/teams/${id}/labels`, { ...o, method: 'POST', body })
};

/* ---------------------------------------------------------------- labels ---- */

export const labels = {
  list: (o: Signal = {}) => request<LabelDto[]>(`${v1}/labels`, o),

  create: (body: CreateLabelRequest, o: Signal = {}) =>
    request<LabelDto>(`${v1}/labels`, { ...o, method: 'POST', body }),

  update: (id: Guid, body: UpdateLabelRequest, o: Signal = {}) =>
    request<LabelDto>(`${v1}/labels/${id}`, { ...o, method: 'PATCH', body }),

  remove: (id: Guid, o: Signal = {}) => request<void>(`${v1}/labels/${id}`, { ...o, method: 'DELETE' })
};

/* -------------------------------------------------------------- projects ---- */

export const projects = {
  list: (
    params: {
      teamId?: Guid;
      status?: string;
      search?: string;
      includeArchived?: boolean;
      page?: number;
      pageSize?: number;
    } = {},
    o: Signal = {}
  ) => request<PagedResult<ProjectDto>>(`${v1}/projects`, { ...o, query: params }),

  get: (id: Guid, o: Signal = {}) => request<ProjectDto>(`${v1}/projects/${id}`, o),

  create: (body: CreateProjectRequest, o: Signal = {}) =>
    request<ProjectDto>(`${v1}/projects`, { ...o, method: 'POST', body }),

  update: (id: Guid, body: UpdateProjectRequest, o: Signal = {}) =>
    request<ProjectDto>(`${v1}/projects/${id}`, { ...o, method: 'PATCH', body }),

  archive: (id: Guid, o: Signal = {}) =>
    request<ProjectDto>(`${v1}/projects/${id}/archive`, { ...o, method: 'POST' }),

  restore: (id: Guid, o: Signal = {}) =>
    request<ProjectDto>(`${v1}/projects/${id}/restore`, { ...o, method: 'POST' }),

  remove: (id: Guid, o: Signal = {}) =>
    request<void>(`${v1}/projects/${id}`, { ...o, method: 'DELETE' }),

  milestones: (id: Guid, o: Signal = {}) => request<MilestoneDto[]>(`${v1}/projects/${id}/milestones`, o),

  createMilestone: (id: Guid, body: CreateMilestoneRequest, o: Signal = {}) =>
    request<MilestoneDto>(`${v1}/projects/${id}/milestones`, { ...o, method: 'POST', body }),

  documents: (id: Guid, o: Signal = {}) =>
    request<DocumentSummary[]>(`${v1}/projects/${id}/documents`, o)
};

export const milestones = {
  get: (id: Guid, o: Signal = {}) => request<MilestoneDto>(`${v1}/milestones/${id}`, o),

  update: (id: Guid, body: UpdateMilestoneRequest, o: Signal = {}) =>
    request<MilestoneDto>(`${v1}/milestones/${id}`, { ...o, method: 'PATCH', body }),

  remove: (id: Guid, o: Signal = {}) =>
    request<void>(`${v1}/milestones/${id}`, { ...o, method: 'DELETE' })
};

/* ------------------------------------------------------------- documents ---- */

export const documents = {
  list: (
    params: {
      teamId?: Guid;
      projectId?: Guid;
      search?: string;
      includeArchived?: boolean;
      page?: number;
      pageSize?: number;
    } = {},
    o: Signal = {}
  ) => request<PagedResult<DocumentSummary>>(`${v1}/documents`, { ...o, query: params }),

  get: (id: Guid, o: Signal = {}) => request<DocumentDto>(`${v1}/documents/${id}`, o),

  create: (body: CreateDocumentRequest, o: Signal = {}) =>
    request<DocumentDto>(`${v1}/documents`, { ...o, method: 'POST', body }),

  update: (id: Guid, body: UpdateDocumentRequest, o: Signal = {}) =>
    request<DocumentDto>(`${v1}/documents/${id}`, { ...o, method: 'PATCH', body }),

  archive: (id: Guid, o: Signal = {}) =>
    request<DocumentDto>(`${v1}/documents/${id}/archive`, { ...o, method: 'POST' }),

  restore: (id: Guid, o: Signal = {}) =>
    request<DocumentDto>(`${v1}/documents/${id}/restore`, { ...o, method: 'POST' }),

  remove: (id: Guid, o: Signal = {}) =>
    request<void>(`${v1}/documents/${id}`, { ...o, method: 'DELETE' })
};

/* ---------------------------------------------------------------- issues ---- */

export const issues = {
  list: (filter: IssueFilter = {}, o: Signal = {}) =>
    request<PagedResult<IssueSummary>>(`${v1}/issues`, {
      ...o,
      query: filter as unknown as QueryParams
    }),

  get: (id: Guid, o: Signal = {}) => request<IssueDetail>(`${v1}/issues/${id}`, o),

  byKey: (key: string, o: Signal = {}) =>
    request<IssueDetail>(`${v1}/issues/by-key/${encodeURIComponent(key)}`, o),

  create: (body: CreateIssueRequest, o: Signal = {}) =>
    request<IssueSummary>(`${v1}/issues`, { ...o, method: 'POST', body }),

  update: (id: Guid, body: UpdateIssueRequest, o: Signal = {}) =>
    request<IssueSummary>(`${v1}/issues/${id}`, { ...o, method: 'PATCH', body }),

  move: (id: Guid, body: MoveIssueRequest, o: Signal = {}) =>
    request<IssueSummary>(`${v1}/issues/${id}/move`, { ...o, method: 'POST', body }),

  archive: (id: Guid, o: Signal = {}) =>
    request<IssueSummary>(`${v1}/issues/${id}/archive`, { ...o, method: 'POST' }),

  restore: (id: Guid, o: Signal = {}) =>
    request<IssueSummary>(`${v1}/issues/${id}/restore`, { ...o, method: 'POST' }),

  remove: (id: Guid, o: Signal = {}) =>
    request<void>(`${v1}/issues/${id}`, { ...o, method: 'DELETE' }),

  activity: (id: Guid, params: { page?: number; pageSize?: number } = {}, o: Signal = {}) =>
    request<PagedResult<ActivityEventDto>>(`${v1}/issues/${id}/activity`, { ...o, query: params }),

  comments: (id: Guid, params: { page?: number; pageSize?: number } = {}, o: Signal = {}) =>
    request<PagedResult<CommentDto>>(`${v1}/issues/${id}/comments`, { ...o, query: params }),

  comment: (id: Guid, body: string, parentCommentId: Guid | null = null, o: Signal = {}) =>
    request<CommentDto>(`${v1}/issues/${id}/comments`, {
      ...o,
      method: 'POST',
      body: { body, parentCommentId }
    }),

  addRelation: (id: Guid, body: CreateIssueRelationRequest, o: Signal = {}) =>
    request<IssueRelationDto>(`${v1}/issues/${id}/relations`, { ...o, method: 'POST', body }),

  removeRelation: (id: Guid, relationId: Guid, o: Signal = {}) =>
    request<void>(`${v1}/issues/${id}/relations/${relationId}`, { ...o, method: 'DELETE' }),

  /** Links an attachment held somewhere else — a share, an object store — by its location. */
  linkAttachment: (id: Guid, body: CreateAttachmentRequest, o: Signal = {}) =>
    request<AttachmentDto>(`${v1}/issues/${id}/attachments`, { ...o, method: 'POST', body }),

  /** Sends the bytes themselves. The API stores them outside the web root, up to 20 MiB. */
  uploadFile: (id: Guid, file: File, o: Signal = {}) =>
    requestRaw<AttachmentDto>(`${v1}/issues/${id}/files`, file, {
      ...o,
      query: { fileName: file.name },
      contentType: file.type || 'application/octet-stream'
    })
};

export const comments = {
  update: (id: Guid, body: string, o: Signal = {}) =>
    request<CommentDto>(`${v1}/comments/${id}`, { ...o, method: 'PATCH', body: { body } }),

  remove: (id: Guid, o: Signal = {}) =>
    request<void>(`${v1}/comments/${id}`, { ...o, method: 'DELETE' })
};

export const attachments = {
  remove: (id: Guid, o: Signal = {}) =>
    request<void>(`${v1}/attachments/${id}`, { ...o, method: 'DELETE' }),

  download: (id: Guid, o: Signal = {}) =>
    requestBlob(`${v1}/attachments/${id}/content`, o),

  /** True for an attachment whose bytes this server holds, as opposed to a link to a share. */
  isStored: (storageUri: string) => storageUri.startsWith('planner-attachment:')
};

/* -------------------------------------------------------------- activity ---- */

export const activity = {
  list: (
    params: { teamId?: Guid; projectId?: Guid; since?: string; page?: number; pageSize?: number } = {},
    o: Signal = {}
  ) => request<PagedResult<ActivityEventDto>>(`${v1}/activity`, { ...o, query: params })
};

/* ---------------------------------------------------------------- health ---- */

export const health = {
  ready: () => request<unknown>('/health/ready', { anonymous: true })
};

/**
 * Walks every page of a list endpoint.
 *
 * Board columns, a team's projects and an issue's comments are all "everything, in order" rather than
 * a page of results, and the alternative — a page size large enough to be safe — is a guess that goes
 * wrong silently the day a team exceeds it.
 */
export async function all<T>(
  fetchPage: (page: number, pageSize: number) => Promise<PagedResult<T>>,
  pageSize = MAX_PAGE_SIZE
): Promise<T[]> {
  const items: T[] = [];
  let page = 1;

  for (;;) {
    const result = await fetchPage(page, pageSize);
    items.push(...result.items);

    if (!result.hasNext || result.items.length === 0) return items;
    page += 1;
  }
}
