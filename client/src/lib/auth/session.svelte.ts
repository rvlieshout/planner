import { me as meApi, ApiError } from '$lib/api';
import { revokeAttachments } from '$lib/markdown/attachments';
import type { Guid, MeResponse, OrgRole, TeamRole } from '$lib/api/types';
import { tokens } from './tokens.svelte';
import { passkeySetupAvailable } from './passkeys';

/*
 * Who is signed in, and what they are allowed to do.
 *
 * The permission ladder below mirrors the server's TeamAccess exactly (docs/roles-and-permissions.md).
 * It decides what this app *offers* — a button nobody may press is worse than no button — and nothing
 * more: the API remains the authority on every write, and a UI that guessed wrong gets a 403 saying
 * what the caller has and what was needed.
 */

export type SessionStatus = 'unknown' | 'restoring' | 'signed-out' | 'signed-in';

/** `None < Read < Comment < Write < Administer`, as integers so "at least" is a comparison. */
export const Permission = {
  None: 0,
  Read: 1,
  Comment: 2,
  Write: 3,
  Administer: 4
} as const;

export type Permission = (typeof Permission)[keyof typeof Permission];

class Session {
  status = $state<SessionStatus>('unknown');
  suggestPasskeySetup = $state(false);
  user = $state<MeResponse | null>(null);

  /** Set when restoring a stored session failed for a reason worth telling the user about. */
  restoreError = $state<string | null>(null);

  /** Team id to membership, for the permission questions the sidebar and toolbars ask constantly. */
  #memberships = $derived(new Map(this.user?.teams.map((t) => [t.teamId, t.role]) ?? []));

  get isSignedIn(): boolean {
    return this.status === 'signed-in' && this.user !== null;
  }

  get role(): OrgRole | null {
    return this.user?.role ?? null;
  }

  get isAdmin(): boolean {
    return this.role === 'owner' || this.role === 'admin';
  }

  get isOwner(): boolean {
    return this.role === 'owner';
  }

  /** A guest is a ceiling, not a starting point: commenting is as far as one ever gets. */
  get isGuest(): boolean {
    return this.role === 'guest';
  }

  get teams(): MeResponse['teams'] {
    return this.user?.teams ?? [];
  }

  /** True when a previous visit left something to resume from, so the shell can try before asking. */
  get canRestore(): boolean {
    return tokens.canRestore;
  }

  teamRole(teamId: Guid | null | undefined): TeamRole | null {
    return teamId ? (this.#memberships.get(teamId) ?? null) : null;
  }

  /** The caller's effective permission on one team, folding the organisation and team roles together. */
  permission(teamId: Guid | null | undefined): Permission {
    if (!this.user) return Permission.None;

    // Admins administer every team, including private ones and ones they are not in. That is the
    // server's rule too: an administrator can add themselves in two calls, so the shorter path is
    // the honest one, and the audit trail records it either way.
    if (this.isAdmin) return Permission.Administer;

    const teamRole = this.teamRole(teamId);
    if (teamRole === null) return Permission.None;

    if (this.isGuest) return Permission.Comment;

    switch (teamRole) {
      case 'Lead':
        return Permission.Administer;
      case 'Member':
        return Permission.Write;
      case 'Viewer':
        return Permission.Read;
      default:
        return Permission.None;
    }
  }

  can(teamId: Guid | null | undefined, required: Permission): boolean {
    return this.permission(teamId) >= required;
  }

  /** Teams this user may change, which is a shorter list than the ones they may read. */
  get administrableTeamIds(): Set<Guid> {
    return new Set(this.teams.filter((t) => this.can(t.teamId, Permission.Administer)).map((t) => t.teamId));
  }

  /** Whether to offer the Teams page at all: an admin may create the first one on an empty install. */
  get canOpenTeamAdmin(): boolean {
    return this.isAdmin || this.administrableTeamIds.size > 0;
  }

  get canOpenUserAdmin(): boolean {
    return this.isAdmin;
  }

  async signInWithPasskey(credential: string): Promise<void> {
    await tokens.signInWithPasskey(credential);
    this.suggestPasskeySetup = false;
    this.user = await meApi.get();
    this.restoreError = null;
    this.status = 'signed-in';
  }

  async signIn(email: string, password: string): Promise<void> {
    await tokens.signIn(email, password);
    this.user = await meApi.get();
    this.suggestPasskeySetup = await passkeySetupAvailable();
    this.restoreError = null;
    this.status = 'signed-in';
  }

  /**
   * Resumes a stored session, or falls to the sign-in screen without complaint.
   *
   * A server that cannot be reached is reported; a token the server refused is not, because that is
   * simply a session that ran out and an explanation nobody asked for.
   */
  async restore(): Promise<boolean> {
    if (!tokens.canRestore) {
      this.status = 'signed-out';
      return false;
    }

    this.status = 'restoring';

    try {
      await tokens.refresh();
      this.user = await meApi.get();
      this.status = 'signed-in';
      return true;
    } catch (error) {
      if (error instanceof ApiError && (error.status === 0 || error.status >= 500)) {
        this.restoreError = error.message;
      }

      this.status = 'signed-out';
      return false;
    }
  }

  /** Called when a request comes back 401 and the refresh could not save it. */
  expire(): void {
    tokens.clear();
    this.suggestPasskeySetup = false;
    this.user = null;
    this.status = 'signed-out';

    // The images of whatever was open are held as blobs in this tab. They are this account's, and
    // the next one to sign in here must not inherit them. Only the resolver is reached from here —
    // the editor itself is a route-level import, and dragging it into the session would put the
    // whole markdown pipeline in the chunk every page loads.
    revokeAttachments();
  }

  signOut(): void {
    this.expire();
    this.restoreError = null;
  }

  /** Applies a profile edit the user just saved, so the footer and avatar update without a reload. */
  applyProfile(profile: MeResponse): void {
    this.user = profile;
  }

  /**
   * Re-reads the profile, which is where team memberships live.
   *
   * A lead who demotes or removes themselves loses that team's page at that moment, because the
   * authority the page was working with has just been handed back. Organisation role and active
   * state are carried in the token instead, so those follow at the next refresh or sign-in.
   */
  async reloadProfile(): Promise<void> {
    if (!this.isSignedIn) return;

    try {
      this.user = await meApi.get();
    } catch {
      // A failed reload leaves the previous profile in place, which is the last thing known to be
      // true. The next request that needs more authority than it grants gets a 403 saying so.
    }
  }
}

export const session = new Session();
