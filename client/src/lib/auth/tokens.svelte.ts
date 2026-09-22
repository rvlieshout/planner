import type { TokenResponse } from '$lib/api/types';
import { ApiError, toApiError } from '$lib/api/errors';

/*
 * The token half of signing in: the grant against /connect/token, and the access token everything
 * else sends.
 *
 * It is separate from the session because the HTTP layer needs a token and the session needs the HTTP
 * layer to fetch a profile. Splitting the two at that seam keeps the dependency pointing one way.
 *
 * Where the refresh token is kept is the one real difference from the desktop client, which encrypts
 * it into a file only its own user can read. A browser has no such vault: localStorage is the only
 * store that survives closing the tab, and the app is deliberately first-party — its own origin, no
 * third-party script, no CDN — so nothing else runs where it could be read. The access token is never
 * written down at all; it lives in memory for the lifetime of the page.
 */

const TOKEN_ENDPOINT = '/connect/token';
const CLIENT_ID = 'planner-web';
const SCOPE = 'openid profile roles offline_access planner.api';
const STORAGE_KEY = 'planner.refreshToken';

/** Refresh this long before the access token actually expires, so a request never races the clock. */
const EXPIRY_MARGIN_MS = 60_000;

class Tokens {
  #access = $state<string | null>(null);
  #refresh = $state<string | null>(null);
  #expiresAt = 0;

  /** In flight refresh, shared so a burst of 401s produces one grant rather than one each. */
  #refreshing: Promise<string> | null = null;

  constructor() {
    this.#refresh = read(STORAGE_KEY);
  }

  /** True when a previous visit left a refresh token to resume from. */
  get canRestore(): boolean {
    return this.#refresh !== null;
  }

  get accessToken(): string | null {
    return this.#access;
  }

  async signIn(email: string, password: string): Promise<void> {
    this.#apply(
      await this.#grant({
        grant_type: 'password',
        username: email,
        password,
        scope: SCOPE
      })
    );
  }

  async signInWithPasskey(credential: string): Promise<void> {
    this.#apply(await this.#grant({
      grant_type: 'urn:planner:params:oauth:grant-type:passkey', credential, scope: SCOPE
    }));
  }

  /** The token to put on the next request, refreshed first if it is spent or about to be. */
  async fresh(): Promise<string | null> {
    if (this.#access && Date.now() < this.#expiresAt - EXPIRY_MARGIN_MS) {
      return this.#access;
    }

    if (!this.#refresh) {
      return this.#access;
    }

    return await this.refresh();
  }

  /** Exchanges the refresh token for a new pair. Concurrent callers share one round trip. */
  async refresh(): Promise<string> {
    this.#refreshing ??= this.#doRefresh().finally(() => {
      this.#refreshing = null;
    });

    return await this.#refreshing;
  }

  async #doRefresh(): Promise<string> {
    const token = this.#refresh;

    if (!token) {
      throw new ApiError(401, 'There is no session to resume.');
    }

    try {
      const response = await this.#grant({
        grant_type: 'refresh_token',
        refresh_token: token,
        scope: SCOPE
      });

      this.#apply(response);
      return response.access_token;
    } catch (error) {
      // A refresh token the server has rejected will not start working on the third attempt, and
      // retrying only hammers the token endpoint. Drop it and let the caller send the user to sign in.
      if (error instanceof ApiError && [400, 401, 403].includes(error.status)) {
        this.clear();
      }

      throw error;
    }
  }

  clear(): void {
    this.#access = null;
    this.#refresh = null;
    this.#expiresAt = 0;
    remove(STORAGE_KEY);
  }

  #apply(response: TokenResponse): void {
    this.#access = response.access_token;
    this.#expiresAt = Date.now() + response.expires_in * 1000;

    // A refresh grant may or may not rotate the refresh token; keep the old one when it does not.
    if (response.refresh_token) {
      this.#refresh = response.refresh_token;
      write(STORAGE_KEY, response.refresh_token);
    }
  }

  async #grant(fields: Record<string, string>): Promise<TokenResponse> {
    const body = new URLSearchParams({ client_id: CLIENT_ID, ...fields });

    let response: Response;

    try {
      response = await fetch(TOKEN_ENDPOINT, {
        method: 'POST',
        headers: { 'content-type': 'application/x-www-form-urlencoded' },
        body
      });
    } catch {
      // Status 0 is "never reached the server", which is a different thing from being refused by it:
      // the refresh token is still good, and the caller should retry rather than sign the user out.
      throw new ApiError(0, 'The server is not reachable. Check the address and your network.');
    }

    if (!response.ok) {
      throw await toOAuthError(response);
    }

    return (await response.json()) as TokenResponse;
  }
}

/**
 * OpenIddict answers the token endpoint with OAuth's own error shape rather than problem details, so
 * a refused password reads as `invalid_grant` unless it is translated here.
 */
async function toOAuthError(response: Response): Promise<ApiError> {
  let payload: { error?: string; error_description?: string } = {};

  try {
    payload = (await response.clone().json()) as typeof payload;
  } catch {
    return await toApiError(response);
  }

  if (payload.error === 'invalid_grant') {
    return new ApiError(401, payload.error_description ?? 'Sign-in failed. Try again or use setup and recovery.');
  }

  if (response.status === 429) {
    return new ApiError(429, 'Too many sign-in attempts. Wait a minute and try again.');
  }

  return new ApiError(
    response.status,
    payload.error_description ?? payload.error ?? 'Sign-in failed.'
  );
}

function read(key: string): string | null {
  try {
    return localStorage.getItem(key);
  } catch {
    // Storage disabled, or a private window that refuses it. The app still works; it just will not
    // resume a session on the next visit.
    return null;
  }
}

function write(key: string, value: string): void {
  try {
    localStorage.setItem(key, value);
  } catch {
    /* as above */
  }
}

function remove(key: string): void {
  try {
    localStorage.removeItem(key);
  } catch {
    /* as above */
  }
}

export const tokens = new Tokens();
