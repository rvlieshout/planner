/** RFC 9457 problem details, as the API writes them. */
export interface ProblemDetails {
  type?: string;
  title?: string;
  status?: number;
  detail?: string;
  instance?: string;
  /** Field name to the messages about it. Validation accumulates, so a field can carry several. */
  errors?: Record<string, string[]>;
}

/**
 * A failed API call.
 *
 * `message` is the API's own wording wherever there is any. That is deliberate: "You can only assign
 * issues to members of the issue's team" beats anything a form could invent, and it is the sentence
 * the server will keep enforcing.
 */
export class ApiError extends Error {
  readonly status: number;
  readonly problem: ProblemDetails | null;

  constructor(status: number, message: string, problem: ProblemDetails | null = null) {
    super(message);
    this.name = 'ApiError';
    this.status = status;
    this.problem = problem;
  }

  /** Field name to its first message, for putting errors beside the inputs that caused them. */
  get fieldErrors(): Record<string, string> {
    const errors = this.problem?.errors ?? {};
    const out: Record<string, string> = {};

    for (const [field, messages] of Object.entries(errors)) {
      if (messages?.length) {
        // The server camel-cases field names in validation output already; lower-casing the first
        // letter anyway means a client can look a field up by the name its form control uses.
        out[field.charAt(0).toLowerCase() + field.slice(1)] = messages[0];
      }
    }

    return out;
  }

  /** Missing or expired credentials, as opposed to being signed in without the authority. */
  get isUnauthorized(): boolean {
    return this.status === 401;
  }

  /** A member, but the action needs more authority. */
  get isForbidden(): boolean {
    return this.status === 403;
  }

  /** Does not exist, or cannot be seen — the API deliberately does not distinguish the two. */
  get isNotFound(): boolean {
    return this.status === 404;
  }

  /** A unique-constraint clash, a guard tripping (last lead, state in use), or a stale write. */
  get isConflict(): boolean {
    return this.status === 409;
  }
}

/** Reads a failed response into an ApiError, falling back to the status when the body is not JSON. */
export async function toApiError(response: Response): Promise<ApiError> {
  let problem: ProblemDetails | null = null;

  try {
    const text = await response.text();

    if (text) {
      const parsed: unknown = JSON.parse(text);

      if (parsed && typeof parsed === 'object') {
        problem = parsed as ProblemDetails;
      }
    }
  } catch {
    // A proxy returning HTML, a truncated body, a network reset mid-read. The status is still news.
  }

  return new ApiError(response.status, describe(response, problem), problem);
}

function describe(response: Response, problem: ProblemDetails | null): string {
  // Validation failures put the useful sentence in `errors`; `title` is the generic wrapper around it.
  const first = Object.values(problem?.errors ?? {}).find((messages) => messages?.length)?.[0];

  return (
    first ??
    problem?.detail ??
    problem?.title ??
    defaults[response.status] ??
    `The server answered ${response.status}.`
  );
}

const defaults: Record<number, string> = {
  400: 'The server rejected that request.',
  401: 'Your session has expired. Sign in again.',
  403: 'You do not have the authority for that.',
  404: 'That does not exist, or you cannot see it.',
  409: 'Someone else changed that first. Reload and try again.',
  429: 'Too many attempts. Wait a minute and try again.',
  500: 'The server failed to handle that. Check its logs.',
  502: 'The server is not reachable.',
  503: 'The server is not reachable.',
  504: 'The server took too long to answer.'
};
