import type { ActivityEventDto, ActivityPurge, Guid, IssuePriority, IssueRelationType, TeamRole } from '$lib/api/types';
import type { IconName } from '$lib/icons/icons';
import { PRIORITY, RELATION, TEAM_ROLE } from '$lib/meta';
import { workspace } from '$lib/workspace.svelte';

/*
 * What an audit event says, in words.
 *
 * The server stores what changed — ids and names, `{ from, to }` — and leaves the wording to the
 * client. This is the one place that wording lives, so the team feed, the inbox and an issue's
 * history can never describe the same change two ways.
 *
 * The sentence leaves out its object. On an issue page "changed status from Todo to Done" is about
 * the issue on screen; in a feed the row names the issue beside it. Users and labels are stored by id
 * and resolved from the workspace's per-team caches, which `prepareActivity` fills first.
 */

export interface Described {
  text: string;
  icon: IconName;
}

type Data = Record<string, unknown>;

const str = (value: unknown): string | null => (typeof value === 'string' && value ? value : null);
const ids = (value: unknown): Guid[] => (Array.isArray(value) ? value.filter((v) => typeof v === 'string') : []);

/** Loads the member and label caches an event list will be described from, one request per team. */
export async function prepareActivity(events: ActivityEventDto[]): Promise<void> {
  const teams = [...new Set(events.map((event) => event.teamId).filter((id): id is Guid => Boolean(id)))];

  await Promise.all(
    teams.flatMap((teamId) => [
      workspace.membersFor(teamId).catch(() => []),
      workspace.labelsFor(teamId).catch(() => [])
    ])
  );
}

function person(teamId: Guid | null, userId: unknown, actorId: Guid): string {
  if (typeof userId !== 'string') return 'someone';
  if (userId === actorId) return 'themselves';

  return workspace.membersNow(teamId).find((member) => member.userId === userId)?.displayName ?? 'someone';
}

function labelNames(teamId: Guid | null, labelIds: Guid[]): string {
  const labels = workspace.labelsNow(teamId);
  const names = labelIds.map((id) => labels.find((label) => label.id === id)?.name).filter(Boolean);

  return names.length > 0 ? names.join(', ') : `${labelIds.length} ${labelIds.length === 1 ? 'label' : 'labels'}`;
}

const FIELD: Record<string, string> = {
  title: 'the title',
  description: 'the description',
  estimate: 'the estimate',
  dueDate: 'the due date',
  parent: 'the parent issue'
};

function list(items: string[]): string {
  if (items.length <= 1) return items.join('');
  return `${items.slice(0, -1).join(', ')} and ${items.at(-1)}`;
}

function fromTo(data: Data, noun: string, verb = 'changed'): string {
  const from = str(data.from);
  const to = str(data.to);

  if (from && to) return `${verb} ${noun} from ${from} to ${to}`;
  if (to) return `set ${noun} to ${to}`;
  if (from) return `cleared ${noun} (was ${from})`;
  return `${verb} ${noun}`;
}

/** True for an event a purge removed — what an open list drops when an administrator clears history. */
export function isPurged(purge: ActivityPurge, event: ActivityEventDto): boolean {
  const inScope = purge.projectId ? event.projectId === purge.projectId : event.teamId === purge.teamId;
  return inScope && (!purge.before || Date.parse(event.createdAt) < Date.parse(purge.before));
}

function purgedText(data: Data, what: string): string {
  const days = typeof data.olderThanDays === 'number' ? data.olderThanDays : null;
  const count = typeof data.deleted === 'number' ? ` (${data.deleted} ${data.deleted === 1 ? 'entry' : 'entries'})` : '';

  return days
    ? `cleared ${what} history older than ${days} ${days === 1 ? 'day' : 'days'}${count}`
    : `cleared ${what} whole history${count}`;
}

export function describeActivity(event: ActivityEventDto): Described {
  const data: Data = event.data ?? {};
  const team = event.teamId;
  const actor = event.actor.id;

  switch (`${event.entityType}:${event.action}`) {
    case 'issue:created':
      return { text: 'created the issue', icon: 'plus' };

    case 'issue:updated': {
      const fields = ids(data.fields);
      const title = data.title as Data | null | undefined;

      if (fields.length === 1 && fields[0] === 'title' && title && str(title.to)) {
        return { text: `renamed it to “${str(title.to)}”`, icon: 'pencil' };
      }

      return { text: `edited ${list(fields.map((field) => FIELD[field] ?? field)) || 'the issue'}`, icon: 'pencil' };
    }

    case 'issue:archived':
      return { text: 'archived the issue', icon: 'archive' };

    case 'issue:restored':
      return { text: 'restored the issue', icon: 'archive-restore' };

    case 'issue:state_changed':
      return { text: fromTo(data, 'status'), icon: 'circle-dot' };

    case 'issue:assignee_changed': {
      const from = data.from ? person(team, data.from, actor) : null;
      const to = data.to ? person(team, data.to, actor) : null;

      if (to === 'themselves' && !from) return { text: 'took the issue', icon: 'circle-user' };
      if (to && from) return { text: `reassigned it from ${from} to ${to}`, icon: 'circle-user' };
      if (to) return { text: `assigned ${to}`, icon: 'circle-user' };
      return { text: `unassigned ${from ?? 'the issue'}`, icon: 'circle-user' };
    }

    case 'issue:priority_changed': {
      const to = str(data.to) as IssuePriority | null;
      return to && to !== 'None'
        ? { text: `set priority to ${PRIORITY[to]?.label ?? to}`, icon: PRIORITY[to]?.icon ?? 'flag' }
        : { text: 'removed the priority', icon: 'minus' };
    }

    case 'issue:labels_changed': {
      const added = ids(data.added);
      const removed = ids(data.removed);

      // Rows written before the server recorded the difference only carry the new set.
      if (!('added' in data)) return { text: 'changed the labels', icon: 'tag' };

      const parts = [
        added.length ? `added ${labelNames(team, added)}` : '',
        removed.length ? `removed ${labelNames(team, removed)}` : ''
      ].filter(Boolean);

      return { text: parts.join(' and ') || 'changed the labels', icon: 'tag' };
    }

    case 'issue:project_changed':
      return str(data.to)
        ? { text: `moved it to project ${str(data.to)}`, icon: 'folder' }
        : { text: `removed it from project ${str(data.from) ?? ''}`.trim(), icon: 'folder' };

    case 'issue:milestone_changed':
      return str(data.to)
        ? { text: `set milestone ${str(data.to)}`, icon: 'milestone' }
        : { text: `removed milestone ${str(data.from) ?? ''}`.trim(), icon: 'milestone' };

    case 'comment:commented':
      return { text: 'commented', icon: 'message-square' };

    case 'attachment:attachment_added':
      return { text: `attached ${str(data.fileName) ?? 'a file'}`, icon: 'paperclip' };

    case 'issueRelation:relation_added': {
      const type = str(data.type) as IssueRelationType | null;
      const wording = type ? RELATION[type]?.outgoing.toLowerCase() : null;
      return { text: wording ? `marked it as ${wording} another issue` : 'added a relation', icon: 'link' };
    }

    case 'issueRelation:relation_removed':
      return { text: 'removed a relation', icon: 'link' };

    case 'project:created':
      return { text: `created project ${str(data.name) ?? ''}`.trim(), icon: 'folder-kanban' };

    case 'project:updated': {
      const status = data.status as Data | undefined;
      return status
        ? { text: fromTo(status, 'the project status'), icon: 'folder-kanban' }
        : { text: 'updated a project', icon: 'folder-kanban' };
    }

    case 'project:archived':
      return { text: 'archived a project', icon: 'archive' };

    case 'project:restored':
      return { text: 'restored a project', icon: 'archive-restore' };

    case 'project:activity_purged':
      return { text: purgedText(data, "the project's"), icon: 'trash-2' };

    case 'team:activity_purged':
      return { text: purgedText(data, "the team's"), icon: 'trash-2' };

    case 'milestone:created':
      return { text: `added milestone ${str(data.name) ?? ''}`.trim(), icon: 'milestone' };

    case 'document:created':
      return { text: `wrote ${str(data.title) ?? 'a document'}`, icon: 'file-text' };

    case 'teamMember:member_added': {
      const role = TEAM_ROLE[str(data.role) as TeamRole]?.label.toLowerCase();
      const who = person(team, event.entityId, actor);
      return { text: `added ${who} to the team${role ? ` as ${role}` : ''}`, icon: 'user-plus' };
    }

    case 'teamMember:member_role_changed':
      return {
        text: `made ${person(team, event.entityId, actor)} a ${TEAM_ROLE[str(data.to) as TeamRole]?.label.toLowerCase() ?? str(data.to) ?? 'different role'}`,
        icon: 'shield'
      };

    case 'teamMember:member_removed':
      return { text: `removed ${person(team, event.entityId, actor)} from the team`, icon: 'users' };

    default:
      return { text: event.action.replaceAll('_', ' '), icon: 'activity' };
  }
}
