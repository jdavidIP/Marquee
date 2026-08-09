/** The claim JwtTokenService writes one of per granted permission. */
const PERMISSION_CLAIM = 'marquee:perm';

/**
 * Reads the permissions out of a bearer token's payload.
 *
 * The signature is deliberately not verified: that is the API's job, and it does it on every
 * request. This exists only to decide what to render — which controls to show, which tab to land on
 * — and is worthless as a security boundary. A tampered token gets whatever UI it likes and is still
 * refused by the server.
 */
export function decodePermissions(token: string | null): string[] {
  if (!token) return [];

  try {
    const payload = token.split('.')[1];
    if (!payload) return [];

    const claim = (JSON.parse(base64UrlDecode(payload)) as Record<string, unknown>)[PERMISSION_CLAIM];

    // A single-valued claim serialises as a bare string and multiple as an array. Today an admin has
    // three, so this is an array — but a future role with exactly one permission would arrive as a
    // string, and `.includes` on a string matches substrings, which would silently grant the wrong
    // thing.
    if (Array.isArray(claim)) return claim.filter((c): c is string => typeof c === 'string');
    return typeof claim === 'string' ? [claim] : [];
  } catch {
    // A malformed token means "no permissions", never an exception thrown during change detection.
    return [];
  }
}

function base64UrlDecode(value: string): string {
  const base64 = value.replace(/-/g, '+').replace(/_/g, '/');
  const padded = base64.padEnd(base64.length + ((4 - (base64.length % 4)) % 4), '=');

  // atob yields one byte per character; usernames and claims may be non-ASCII, so the bytes are
  // re-read as UTF-8 rather than assumed to be Latin-1.
  const bytes = Uint8Array.from(atob(padded), (c) => c.charCodeAt(0));
  return new TextDecoder().decode(bytes);
}
