import {
  HubConnectionBuilder,
  HubConnectionState,
  LogLevel,
  type HubConnection
} from '@microsoft/signalr';
import { tokens } from '$lib/auth/tokens.svelte';
import type {
  AttachmentDto,
  CommentDto,
  DocumentSummary,
  EntityChange,
  Guid,
  IssueRelationDto,
  IssueSummary,
  LabelDto,
  MilestoneDto,
  ProjectDto,
  TeamDto,
  TeamMemberDto,
  UserSummary,
  WorkflowStateDto
} from '$lib/api/types';

/*
 * The live change feed.
 *
 * One connection for the whole application, because the server puts a connection into a group per
 * team the caller can read and publishes each change to exactly one of them — a second connection
 * would receive the same events again, not different ones.
 *
 * The browser cannot set an Authorization header on a WebSocket handshake, so the token goes in the
 * query string and the API's SignalRAuthenticationMiddleware lifts it back into a header before
 * authentication runs. `accessTokenFactory` is asked again on every reconnect, which is what keeps a
 * long-lived socket working across an access token's expiry.
 */

export type RealtimeStatus = 'idle' | 'connecting' | 'connected' | 'reconnecting';

/** Server-to-client methods, mirroring IPlannerClient in Planner.Contracts. */
export interface RealtimeEvents {
  TeamChanged: EntityChange<TeamDto>;
  TeamMemberChanged: EntityChange<TeamMemberDto>;
  WorkflowStateChanged: EntityChange<WorkflowStateDto>;
  LabelChanged: EntityChange<LabelDto>;
  ProjectChanged: EntityChange<ProjectDto>;
  MilestoneChanged: EntityChange<MilestoneDto>;
  DocumentChanged: EntityChange<DocumentSummary>;
  IssueChanged: EntityChange<IssueSummary>;
  CommentChanged: EntityChange<CommentDto>;
  AttachmentChanged: EntityChange<AttachmentDto>;
  IssueRelationChanged: EntityChange<IssueRelationDto>;
  UserChanged: EntityChange<UserSummary>;
}

export type RealtimeEvent = keyof RealtimeEvents;

const EVENTS: RealtimeEvent[] = [
  'TeamChanged',
  'TeamMemberChanged',
  'WorkflowStateChanged',
  'LabelChanged',
  'ProjectChanged',
  'MilestoneChanged',
  'DocumentChanged',
  'IssueChanged',
  'CommentChanged',
  'AttachmentChanged',
  'IssueRelationChanged',
  'UserChanged'
];

type Handler<E extends RealtimeEvent> = (change: RealtimeEvents[E]) => void;

class Realtime {
  /** Green in the status bar while this is `connected`. Everything still works when it is not. */
  status = $state<RealtimeStatus>('idle');

  /** Groups the server says it granted, rather than the ones this client asked for. */
  groups = $state<string[]>([]);

  #connection: HubConnection | null = null;
  #starting: Promise<void> | null = null;

  // Handlers live here rather than on the connection so a reconnect — or a sign-out and back in —
  // does not require every open view to re-register.
  readonly #handlers = new Map<RealtimeEvent, Set<Handler<RealtimeEvent>>>();

  /** Issues with an open detail page. Re-joined after a reconnect, which drops group membership. */
  readonly #issueSubscriptions = new Set<Guid>();

  #reconnectHandlers = new Set<() => void>();

  get isConnected(): boolean {
    return this.status === 'connected';
  }

  /** Subscribes to one server event. Returns the function that unsubscribes it. */
  on<E extends RealtimeEvent>(event: E, handler: Handler<E>): () => void {
    const set = this.#handlers.get(event) ?? new Set();
    set.add(handler as Handler<RealtimeEvent>);
    this.#handlers.set(event, set);

    return () => set.delete(handler as Handler<RealtimeEvent>);
  }

  /**
   * Runs after the socket comes back up.
   *
   * A reconnect is not a no-op: the connection was deaf for as long as it was down, so whatever a
   * view is showing may have moved on. Views use this to refetch rather than to resume.
   */
  onReconnected(handler: () => void): () => void {
    this.#reconnectHandlers.add(handler);
    return () => this.#reconnectHandlers.delete(handler);
  }

  async connect(): Promise<void> {
    if (this.#connection) return await (this.#starting ?? Promise.resolve());

    const connection = new HubConnectionBuilder()
      .withUrl('/hubs/planner', {
        accessTokenFactory: async () => (await tokens.fresh()) ?? ''
      })
      // Four quick attempts, then every thirty seconds forever. A laptop that was closed over a
      // weekend should find its way back without anyone reloading the page.
      .withAutomaticReconnect({
        nextRetryDelayInMilliseconds: (context) =>
          [0, 2_000, 5_000, 10_000][context.previousRetryCount] ?? 30_000
      })
      .configureLogging(LogLevel.Warning)
      .build();

    for (const event of EVENTS) {
      connection.on(event, (change: RealtimeEvents[RealtimeEvent]) => {
        for (const handler of this.#handlers.get(event) ?? []) {
          try {
            handler(change);
          } catch (error) {
            // One view throwing must not stop the change reaching the others.
            console.error(`[realtime] ${event} handler failed`, error);
          }
        }
      });
    }

    connection.on('Subscribed', (groups: string[]) => {
      this.groups = groups;
    });

    connection.onreconnecting(() => {
      this.status = 'reconnecting';
    });

    connection.onreconnected(async () => {
      this.status = 'connected';

      // Group membership does not survive a reconnect, so anything joined on demand is joined again.
      for (const issueId of this.#issueSubscriptions) {
        await connection.invoke('SubscribeToIssue', issueId).catch(() => {});
      }

      for (const handler of this.#reconnectHandlers) handler();
    });

    connection.onclose(() => {
      this.status = 'idle';
    });

    this.#connection = connection;
    this.status = 'connecting';

    this.#starting = connection
      .start()
      .then(() => {
        this.status = 'connected';
      })
      .catch((error: unknown) => {
        // A board that cannot open a socket is as fresh as its last fetch, which is a degraded view
        // rather than a broken one. Nothing is thrown at the caller.
        console.warn('[realtime] could not connect', error);
        this.status = 'idle';
      })
      .finally(() => {
        this.#starting = null;
      });

    await this.#starting;
  }

  async disconnect(): Promise<void> {
    const connection = this.#connection;
    this.#connection = null;
    this.#issueSubscriptions.clear();
    this.groups = [];
    this.status = 'idle';

    if (connection) await connection.stop().catch(() => {});
  }

  /** Opts into comments, attachments and relations for one issue. False means the server refused. */
  async subscribeToIssue(issueId: Guid): Promise<boolean> {
    this.#issueSubscriptions.add(issueId);

    if (this.#connection?.state !== HubConnectionState.Connected) return false;

    try {
      return await this.#connection.invoke<boolean>('SubscribeToIssue', issueId);
    } catch {
      return false;
    }
  }

  async unsubscribeFromIssue(issueId: Guid): Promise<void> {
    this.#issueSubscriptions.delete(issueId);

    if (this.#connection?.state !== HubConnectionState.Connected) return;

    await this.#connection.invoke('UnsubscribeFromIssue', issueId).catch(() => {});
  }

  /** Re-evaluates team groups after a membership change, without dropping the connection. */
  async resubscribe(): Promise<void> {
    if (this.#connection?.state !== HubConnectionState.Connected) return;

    try {
      this.groups = await this.#connection.invoke<string[]>('Resubscribe');
    } catch {
      /* The next reconnect rebuilds them anyway. */
    }
  }
}

export const realtime = new Realtime();
