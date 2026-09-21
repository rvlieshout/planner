/*
 * Per-browser preferences: the theme, the sidebar, the team and the email last used.
 *
 * The desktop client keeps the same handful of things in settings.json beside its session file. None
 * of it belongs on the server — it describes this machine, not this account — so it lives in
 * localStorage, and every read tolerates storage being unavailable, because a private window refuses
 * it outright and an app that throws on start is worse than one that forgets a preference.
 */

export type ThemeChoice = 'system' | 'light' | 'dark';

/** How a team's or project's board is drawn: columns of cards, or groups of rows. */
export type BoardViewChoice = 'board' | 'list';

const KEYS = {
  theme: 'planner.theme',
  dateLocale: 'planner.dateLocale',
  sidebarWidth: 'planner.sidebarWidth',
  sidebarCollapsed: 'planner.sidebarCollapsed',
  lastTeamId: 'planner.lastTeamId',
  boardView: 'planner.boardView',
  lastEmail: 'planner.lastEmail'
} as const;

const SIDEBAR_MIN = 180;
const SIDEBAR_MAX = 420;

class Settings {
  dateLocale = $state('auto');
  theme = $state<ThemeChoice>('system');
  sidebarWidth = $state(236);
  sidebarCollapsed = $state(false);
  lastTeamId = $state<string | null>(null);

  /**
   * One choice for every board rather than one per board.
   *
   * Someone who reads boards as lists reads all of them that way, and a preference that has to be set
   * again on each project is one nobody sets at all.
   */
  boardView = $state<BoardViewChoice>('board');

  /** Pre-fills the sign-in form. The password is never stored, by anyone, anywhere. */
  lastEmail = $state<string | null>(null);

  constructor() {
    const dateLocale = read(KEYS.dateLocale);
    if (dateLocale && ['auto', 'nl-NL', 'en-GB', 'en-US', 'de-DE', 'fr-FR'].includes(dateLocale)) {
      this.dateLocale = dateLocale;
    }
    const theme = read(KEYS.theme);
    if (theme === 'light' || theme === 'dark' || theme === 'system') this.theme = theme;

    const width = Number(read(KEYS.sidebarWidth));
    if (Number.isFinite(width) && width > 0) this.sidebarWidth = clampSidebar(width);

    this.sidebarCollapsed = read(KEYS.sidebarCollapsed) === 'true';
    this.lastTeamId = read(KEYS.lastTeamId);

    const boardView = read(KEYS.boardView);
    if (boardView === 'board' || boardView === 'list') this.boardView = boardView;
    this.lastEmail = read(KEYS.lastEmail);
  }

  setTheme(theme: ThemeChoice): void {
    this.theme = theme;
    write(KEYS.theme, theme);
    this.applyTheme();
  }

  setDateLocale(locale: string): void {
    this.dateLocale = locale;
    write(KEYS.dateLocale, locale);
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

  setBoardView(view: BoardViewChoice): void {
    this.boardView = view;
    write(KEYS.boardView, view);
  }

  toggleBoardView(): void {
    this.setBoardView(this.boardView === 'board' ? 'list' : 'board');
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
