import { all, issues as issuesApi, projects as projectsApi, teams as teamsApi } from '$lib/api';
import type {
  Guid,
  LabelDto,
  ProjectDto,
  TeamDto,
  TeamMemberDto,
  WorkflowStateDto
} from '$lib/api/types';
import { session } from '$lib/auth/session.svelte';
import { compareRank } from '$lib/rank';
import { realtime } from '$lib/realtime/hub.svelte';
import { settings } from '$lib/settings.svelte';

/*
 * What the whole application needs to know at once: the teams you can read, which one you are looking
 * at, and — per team — its workflow states, its members and its labels.
 *
 * These are cached per team rather than refetched per view because they are asked for constantly and
 * change rarely: every board column, every assignee picker, every label chip and every drag onto a
 * My Issues group reads one of them. The socket keeps them honest, so the cache never goes stale
 * without being told.
 */

class Workspace {
  /** Every team the caller can read, in the order the API returns them. */
  teams = $state<TeamDto[]>([]);

  currentTeamId = $state<Guid | null>(null);

  /** Projects of the current team. The sidebar lists them and the board filters by them. */
  projects = $state<ProjectDto[]>([]);

  loading = $state(false);
  error = $state<string | null>(null);

  /**
   * Whether this session has loaded the workspace yet, successfully or not.
   *
   * The shell keys on this rather than on an empty team list, because an empty list is a legitimate
   * answer: an account in no team, or one just removed from its last. Asking again whenever the list
   * is empty turns that answer into a request loop.
   */
  initialized = $state(false);

  #states = $state<Record<Guid, WorkflowStateDto[]>>({});
  #members = $state<Record<Guid, TeamMemberDto[]>>({});
  #labels = $state<Record<Guid, LabelDto[]>>({});
  /** Projects of teams other than the current one, for an issue of another team opened from My Issues. */
  #projects = $state<Record<Guid, ProjectDto[]>>({});

  /** In-flight loads, so eight cards asking for the same team's states make one request. */
  readonly #pending = new Map<string, Promise<unknown>>();

  #wired = false;

  get currentTeam(): TeamDto | null {
    return this.teams.find((team) => team.id === this.currentTeamId) ?? null;
  }

  get hasTeams(): boolean {
    return this.teams.length > 0;
  }

  /* ------------------------------------------------------------- loading ---- */

  /** Loads the team list and settles on a current team. Called once, after signing in. */
  async initialize(): Promise<void> {
    this.initialized = true;
    this.loading = true;
    this.error = null;

    // Wired before the first request, so a failed load still hears about a team it is added to later
    // and recovers on the next reconnect rather than never.
    this.#wire();

    try {
      this.teams = await teamsApi.list();

      // The remembered team, if it is still one this account can read; otherwise the first.
      const remembered = settings.lastTeamId;
      const team =
        this.teams.find((t) => t.id === remembered) ?? this.teams.find((t) => !t.archivedAt) ?? this.teams[0];

      await this.setTeam(team?.id ?? null);
    } catch (error) {
      this.error = error instanceof Error ? error.message : 'Could not load your teams.';
    } finally {
      this.loading = false;
    }
  }

  async setTeam(teamId: Guid | null): Promise<void> {
    if (this.currentTeamId === teamId && this.projects.length > 0) return;

    this.currentTeamId = teamId;
    settings.setLastTeam(teamId);
    this.projects = [];

    if (!teamId) return;

    await Promise.all([this.statesFor(teamId), this.loadProjects(teamId)]);
  }

  async loadProjects(teamId: Guid | null = this.currentTeamId): Promise<void> {
    if (!teamId) return;

    const loaded = await all((page, pageSize) => projectsApi.list({ teamId, page, pageSize }));

    // Guard against a slow answer for a team the user has already navigated away from.
    if (this.currentTeamId === teamId) this.projects = loaded;
  }

  /** Re-reads the team list, for a create, rename, recolour, archive or restore. */
  async refreshTeams(): Promise<void> {
    this.teams = await teamsApi.list();

    // Also when there was no current team at all: someone added to their first team lands in it.
    if (!this.teams.some((team) => team.id === this.currentTeamId)) {
      await this.setTeam(this.teams[0]?.id ?? null);
    }
  }

  /* --------------------------------------------------------- team caches ---- */

  statesFor(teamId: Guid): Promise<WorkflowStateDto[]> {
    return this.#cached('states', teamId, this.#states, () => teamsApi.states(teamId));
  }

  membersFor(teamId: Guid): Promise<TeamMemberDto[]> {
    return this.#cached('members', teamId, this.#members, () => teamsApi.members(teamId));
  }

  labelsFor(teamId: Guid): Promise<LabelDto[]> {
    return this.#cached('labels', teamId, this.#labels, () => teamsApi.labels(teamId));
  }

  /**
   * A team's unarchived projects, for an issue form's project picker.
   *
   * The current team's are the list the sidebar already holds; any other team's are fetched once and
   * cached, because an issue opened from My Issues can belong to any team you read.
   */
  async projectsFor(teamId: Guid): Promise<ProjectDto[]> {
    if (teamId === this.currentTeamId && this.projects.length > 0) return this.projectsNow(teamId);

    const loaded = await this.#cached('projects', teamId, this.#projects, () =>
      all((page, pageSize) => projectsApi.list({ teamId, page, pageSize }))
    );

    return loaded.filter((project) => !project.archivedAt);
  }

  /** The cached projects of a team, current or not, for a render that cannot wait for a promise. */
  projectsNow(teamId: Guid | null | undefined): ProjectDto[] {
    if (!teamId) return [];

    const projects =
      teamId === this.currentTeamId && this.projects.length > 0 ? this.projects : this.#projects[teamId];

    return (projects ?? []).filter((project) => !project.archivedAt);
  }

  /**
   * The project an issue belongs to, for a card or row that names it.
   *
   * Only the current team's projects are loaded, which is exactly the set a board draws from. An
   * issue of another team — My Issues shows those — resolves to null and simply goes unnamed rather
   * than provoking a request per row.
   */
  projectNow(projectId: Guid | null | undefined): ProjectDto | null {
    return projectId ? (this.projects.find((project) => project.id === projectId) ?? null) : null;
  }

  /** Whatever is already cached, for a render that cannot wait for a promise. */
  statesNow(teamId: Guid | null | undefined): WorkflowStateDto[] {
    return teamId ? (this.#states[teamId] ?? []) : [];
  }

  membersNow(teamId: Guid | null | undefined): TeamMemberDto[] {
    return teamId ? (this.#members[teamId] ?? []) : [];
  }

  labelsNow(teamId: Guid | null | undefined): LabelDto[] {
    return teamId ? (this.#labels[teamId] ?? []) : [];
  }

  /**
   * The state a My Issues drop lands in.
   *
   * Its groups are state *types*, because those issues come from several teams whose columns do not
   * line up. So a drop resolves to that issue's own team's first state of the type — the same "Done"
   * the team's own board would have moved it to.
   */
  async stateOfType(teamId: Guid, type: WorkflowStateDto['type']): Promise<WorkflowStateDto | null> {
    const states = await this.statesFor(teamId);

    return states.filter((state) => state.type === type).sort((a, b) => compareRank(a.rank, b.rank))[0] ?? null;
  }

  /** Drops a team's caches, after its states, labels or membership changed. */
  invalidate(teamId: Guid): void {
    delete this.#states[teamId];
    delete this.#members[teamId];
    delete this.#labels[teamId];
  }

  /** Drops every team's labels, after an organisation-wide one changed — each team caches those. */
  invalidateLabels(): void {
    this.#labels = {};
  }

  async #cached<T>(
    kind: string,
    teamId: Guid,
    store: Record<Guid, T[]>,
    load: () => Promise<T[]>
  ): Promise<T[]> {
    const cached = store[teamId];
    if (cached) return cached;

    const key = `${kind}:${teamId}`;
    const pending = this.#pending.get(key) as Promise<T[]> | undefined;
    if (pending) return await pending;

    const request = load()
      .then((items) => {
        store[teamId] = items;
        return items;
      })
      .finally(() => this.#pending.delete(key));

    this.#pending.set(key, request);
    return await request;
  }

  /* -------------------------------------------------------------- live ---- */

  /**
   * Keeps the cached shape of the workspace current.
   *
   * The desktop client subscribes to issue traffic alone and picks up a new project on the next
   * refresh. Everything in this store is cheap to apply in place, so all of it is wired: a team
   * someone renames, a project someone creates, a column someone adds and a member someone removes
   * all land without a reload.
   */
  #wire(): void {
    if (this.#wired) return;
    this.#wired = true;

    realtime.on('TeamChanged', (change) => {
      if (change.kind === 'Deleted') {
        this.teams = this.teams.filter((team) => team.id !== change.id);
        return;
      }

      if (!change.entity) return;
      const entity = change.entity;
      const index = this.teams.findIndex((team) => team.id === entity.id);

      this.teams =
        index >= 0
          ? this.teams.map((team) => (team.id === entity.id ? entity : team))
          : [...this.teams, entity];
    });

    /*
     * A project, including the issue rollup the sidebar counts beside its name.
     *
     * The API republishes a project whenever an issue write moves that rollup — it is counted from
     * issues on read, so it changes without the project row changing — which is what keeps those
     * counts live without this store holding a single issue.
     */
    realtime.on('ProjectChanged', (change) => {
      // Another team's cached projects are patched the same way, so an open form of that team keeps up.
      if (change.teamId && change.teamId !== this.currentTeamId) {
        const cached = this.#projects[change.teamId];
        if (!cached) return;

        const entity = change.entity;
        const gone = change.kind === 'Deleted' || change.kind === 'Archived' || !entity || entity.archivedAt;

        this.#projects[change.teamId] = gone
          ? cached.filter((project) => project.id !== change.id)
          : cached.some((project) => project.id === entity.id)
            ? cached.map((project) => (project.id === entity.id ? entity : project))
            : [...cached, entity];
        return;
      }

      if (change.teamId !== this.currentTeamId) return;

      if (change.kind === 'Deleted' || change.kind === 'Archived') {
        this.projects = this.projects.filter((project) => project.id !== change.id);
        return;
      }

      if (!change.entity) return;
      const entity = change.entity;

      // `projects` is the unarchived list, so an update to an archived one is news that it does not
      // belong here — not an invitation to add it. Archiving arrives as its own kind above, but an
      // issue write against an already-archived project, or a rename of one, arrives as an update.
      if (entity.archivedAt) {
        this.projects = this.projects.filter((project) => project.id !== entity.id);
        return;
      }

      const index = this.projects.findIndex((project) => project.id === entity.id);

      this.projects =
        index >= 0
          ? this.projects.map((project) => (project.id === entity.id ? entity : project))
          : [...this.projects, entity];
    });

    // These three invalidate rather than patch. They are read through a promise anyway, they change
    // rarely, and a column list rebuilt from the server cannot end up in an order nobody chose.
    realtime.on('WorkflowStateChanged', (change) => {
      if (change.teamId) delete this.#states[change.teamId];
    });

    realtime.on('LabelChanged', (change) => {
      if (change.teamId) delete this.#labels[change.teamId];
    });

    realtime.on('TeamMemberChanged', async (change) => {
      if (change.teamId) delete this.#members[change.teamId];

      // A membership change that involves this user changes which teams they can read at all, so the
      // socket's own groups have to be re-evaluated as well as this store — and the profile too,
      // because that is where the team roles every permission check reads from live.
      if (change.entity?.userId === session.user?.id) {
        await session.reloadProfile();
        await realtime.resubscribe();
        await this.refreshTeams();
      }
    });

    realtime.onReconnected(async () => {
      // The socket was deaf for as long as it was down. The team list is the cheapest thing to be
      // wrong about and the most visible, so it is the one thing refetched unconditionally.
      await this.refreshTeams();

      this.#projects = {};

      if (this.currentTeamId) {
        this.invalidate(this.currentTeamId);
        await this.loadProjects();
      }
    });
  }

  /** Clears everything on sign-out, so a second account never sees the first one's workspace. */
  reset(): void {
    this.teams = [];
    this.projects = [];
    this.currentTeamId = null;
    this.#states = {};
    this.#members = {};
    this.#labels = {};
    this.#projects = {};
    this.error = null;
    this.initialized = false;
  }
}

export const workspace = new Workspace();

/** Convenience for the pickers: every issue assignable to, for one team. */
export async function assignableMembers(teamId: Guid): Promise<TeamMemberDto[]> {
  const members = await workspace.membersFor(teamId);
  return [...members].sort((a, b) => a.displayName.localeCompare(b.displayName));
}

/** Preloads what an issue form needs, in one round of requests rather than four in sequence. */
export async function loadIssueFormData(teamId: Guid) {
  const [states, members, labels, projects] = await Promise.all([
    workspace.statesFor(teamId),
    workspace.membersFor(teamId),
    workspace.labelsFor(teamId),
    workspace.projectsFor(teamId)
  ]);

  return { states, members, labels, projects };
}

/** The issues a board shows, in the exact order the board renders them. */
export function loadBoardIssues(teamId: Guid, projectId?: Guid, signal?: AbortSignal) {
  return all((page, pageSize) =>
    issuesApi.list({ teamId, projectId, sort: 'board', page, pageSize }, { signal })
  );
}
