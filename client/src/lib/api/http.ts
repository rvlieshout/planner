import { tokens } from '$lib/auth/tokens.svelte';
import { ApiError, toApiError } from './errors';

/*
 * The one place a request leaves this application.
 *
 * Paths are same-origin and absolute — `/api/v1/issues`, not an address the user configured. In
 * production Caddy serves this app and proxies everything it does not recognise to the API container,
 * and in development Vite proxies the same three prefixes; either way the browser only ever talks to
 * the origin it came from, which is also why none of this needs CORS.
 */

export interface RequestOptions {
  method?: 'GET' | 'POST' | 'PATCH' | 'PUT' | 'DELETE';
  /** Serialized as JSON. Undefined means no body, which is not the same as `null`. */
  body?: unknown;
  query?: QueryParams;
  signal?: AbortSignal;
  /** Skips the bearer token — only the anonymous endpoints (health) want this. */
  anonymous?: boolean;
}

export type QueryValue = string | number | boolean | null | undefined;
export type QueryParams = Record<string, QueryValue | readonly QueryValue[]>;

/**
 * Builds a query string the way the API reads one: an array becomes the same key repeated, which is
 * an OR set on the server, and empty values are dropped rather than sent as blanks.
 */
export function queryString(params: QueryParams | undefined): string {
  if (!params) return '';

  const search = new URLSearchParams();

  for (const [key, value] of Object.entries(params)) {
    for (const item of Array.isArray(value) ? value : [value]) {
      if (item === null || item === undefined || item === '') continue;
      search.append(key, String(item));
    }
  }

  const text = search.toString();
  return text ? `?${text}` : '';
}

/** Raised when the session cannot be renewed, so the shell can route to sign-in exactly once. */
export type UnauthorizedHandler = () => void;

let onUnauthorized: UnauthorizedHandler | null = null;

/**
 * Registered by the shell. The HTTP layer knows *when* a session has ended; only the layer above it
 * can decide what to put on the screen about it.
 */
export function setUnauthorizedHandler(handler: UnauthorizedHandler | null): void {
  onUnauthorized = handler;
}

export async function request<T>(path: string, options: RequestOptions = {}): Promise<T> {
  const response = await send(path, options);

  if (response.status === 204 || response.headers.get('content-length') === '0') {
    return undefined as T;
  }

  const text = await response.text();
  return (text ? JSON.parse(text) : undefined) as T;
}

/** For the endpoints that answer with bytes rather than JSON — attachment downloads. */
export async function requestBlob(path: string, options: RequestOptions = {}): Promise<Blob> {
  return await (await send(path, options)).blob();
}

/** For uploads, where the body is the file itself rather than a JSON document. */
export async function requestRaw<T>(
  path: string,
  body: BodyInit,
  options: RequestOptions & { contentType?: string } = {}
): Promise<T> {
  const response = await send(path, { ...options, method: options.method ?? 'POST' }, body, {
    'content-type': options.contentType ?? 'application/octet-stream'
  });

  const text = await response.text();
  return (text ? JSON.parse(text) : undefined) as T;
}

async function send(
  path: string,
  options: RequestOptions,
  rawBody?: BodyInit,
  extraHeaders?: Record<string, string>
): Promise<Response> {
  const url = path + queryString(options.query);
  const attempt = async (token: string | null) => {
    const headers: Record<string, string> = { accept: 'application/json', ...extraHeaders };

    if (token) headers.authorization = `Bearer ${token}`;

    const hasJsonBody = rawBody === undefined && options.body !== undefined;
    if (hasJsonBody) headers['content-type'] = 'application/json';

    try {
      return await fetch(url, {
        method: options.method ?? 'GET',
        headers,
        body: rawBody ?? (hasJsonBody ? JSON.stringify(options.body) : undefined),
        signal: options.signal
      });
    } catch (error) {
      // An aborted request is a view being replaced, not a failure worth reporting.
      if (error instanceof DOMException && error.name === 'AbortError') throw error;
      throw new ApiError(0, 'The server is not reachable. Check your network.');
    }
  };

  const token = options.anonymous ? null : await tokens.fresh();
  let response = await attempt(token);

  // Exactly one refresh-and-retry. One, not a loop: a refresh the server has already rejected will not
  // start working on the third attempt, and retrying would only hammer the token endpoint.
  if (response.status === 401 && !options.anonymous) {
    try {
      response = await attempt(await tokens.refresh());
    } catch {
      onUnauthorized?.();
      throw new ApiError(401, 'Your session has expired. Sign in again.');
    }

    if (response.status === 401) {
      tokens.clear();
      onUnauthorized?.();
    }
  }

  if (!response.ok) {
    throw await toApiError(response);
  }

  return response;
}
