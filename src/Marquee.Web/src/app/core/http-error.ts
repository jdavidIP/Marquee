/**
 * Turns a failed HttpClient call into something worth showing a person.
 *
 * Every Marquee error response carries `{ error: "..." }` — the admin conflicts, the clap endpoint,
 * the auth endpoints — and those messages are written to be read. Preferring them over a generic
 * string is what makes "This Premiere has already started" reach the screen instead of "Something
 * went wrong".
 */
export function apiError(err: unknown, fallback: string): string {
  const e = err as { status?: number; error?: { error?: string } };

  if (e?.error?.error) return e.error.error;

  switch (e?.status) {
    case 0:
      return 'Cannot reach the server. Is the API running?';
    case 401:
      return 'Your session has expired. Sign in again.';
    case 403:
      return 'You do not have permission to do that.';
    case 404:
      return 'That item no longer exists.';
    default:
      return fallback;
  }
}

/** True when the server flagged a refusal the caller may retry with an explicit override (§4.6). */
export function isCooldownConflict(err: unknown): boolean {
  const e = err as { status?: number; error?: { cooldown?: boolean } };
  return e?.status === 409 && e?.error?.cooldown === true;
}
