/*
 * Per-browser preferences: the theme, the sidebar, the team and the email last used.
 *
 * The desktop client keeps the same handful of things in settings.json beside its session file. None
 * of it belongs on the server — it describes this machine, not this account — so it lives in
 * localStorage, and every read tolerates storage being unavailable, because a private window refuses
 * it outright and an app that throws on start is worse than one that forgets a preference.
 */

export type ThemeChoice = 'system' | 'light' | 'dark';

const KEYS = {
  theme: 'planner.theme',
  sidebarWidth: 'planner.sidebarWidth',
  sidebarCollapsed: 'planner.sidebarCollapsed',
  lastTeamId: 'planner.lastTeamId',
  lastEmail: 'planner.lastEmail'
} as const;

const SIDEBAR_MIN = 180;
const SIDEBAR_MAX = 420;

class Settings {
  theme = $state<ThemeChoice>('system');
  sidebarWidth = $state(236);
  sidebarCollapsed = $state(false);
  lastTeamId = $state<string | null>(null);

  /** Pre-fills the sign-in form. The password is never stored, by anyone, anywhere. */
  lastEmail = $state<string | null>(null);

  constructor() {
    const theme = read(KEYS.theme);
    if (theme === 'light' || theme === 'dark' || theme === 'system') this.theme = theme;

    const width = Number(read(KEYS.sidebarWidth));
    if (Number.isFinite(width) && width > 0) this.sidebarWidth = clampSidebar(width);

    this.sidebarCollapsed = read(KEYS.sidebarCollapsed) === 'true';
    this.lastTeamId = read(KEYS.lastTeamId);
    this.lastEmail = read(KEYS.lastEmail);
  }

  setTheme(theme: ThemeChoice): void {
    this.theme = theme;
    write(KEYS.theme, theme);
    this.applyTheme();
  }

  /**
   * Stamps the choice onto <html>, or removes it so the OS setting decides.
   *
   * Both the explicit attribute and the `prefers-color-scheme` block are defined in tokens.css, and
   * the attribute wins in either direction — so choosing light on a dark desktop works as well as the
   * other way round.
   */
  applyTheme(): void {
    const root = document.documentElement;

    if (this.theme === 'system') {
      root.removeAttribute('data-theme');
    } else {
      root.setAttribute('data-theme', this.theme);
    }
  }

  setSidebarWidth(width: number): void {
    this.sidebarWidth = clampSidebar(width);
    write(KEYS.sidebarWidth, String(this.sidebarWidth));
  }

  toggleSidebar(): void {
    this.sidebarCollapsed = !this.sidebarCollapsed;
    write(KEYS.sidebarCollapsed, String(this.sidebarCollapsed));
  }

  setLastTeam(teamId: string | null): void {
    this.lastTeamId = teamId;

    if (teamId) {
      write(KEYS.lastTeamId, teamId);
    } else {
      remove(KEYS.lastTeamId);
    }
  }

  setLastEmail(email: string): void {
    this.lastEmail = email;
    write(KEYS.lastEmail, email);
  }
}

const clampSidebar = (width: number) => Math.min(Math.max(Math.round(width), SIDEBAR_MIN), SIDEBAR_MAX);

function read(key: string): string | null {
  try {
    return localStorage.getItem(key);
  } catch {
    return null;
  }
}

function write(key: string, value: string): void {
  try {
    localStorage.setItem(key, value);
  } catch {
    /* Storage unavailable: the preference is simply not remembered. */
  }
}

function remove(key: string): void {
  try {
    localStorage.removeItem(key);
  } catch {
    /* as above */
  }
}

export const settings = new Settings();
export { SIDEBAR_MIN, SIDEBAR_MAX };
