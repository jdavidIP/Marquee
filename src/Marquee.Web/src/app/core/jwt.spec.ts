import { decodePermissions } from './jwt';

/** Builds a token with the given payload. Signature is irrelevant — nothing here verifies it. */
function tokenWith(payload: Record<string, unknown>): string {
  const encode = (value: string) =>
    btoa(String.fromCharCode(...new TextEncoder().encode(value)))
      .replace(/\+/g, '-')
      .replace(/\//g, '_')
      .replace(/=+$/, '');

  return `${encode(JSON.stringify({ alg: 'HS256' }))}.${encode(JSON.stringify(payload))}.signature`;
}

describe('decodePermissions', () => {
  it('reads an array claim', () => {
    const token = tokenWith({ 'marquee:perm': ['premieres:manage', 'users:view', 'users:block'] });

    expect(decodePermissions(token)).toEqual(['premieres:manage', 'users:view', 'users:block']);
  });

  it('reads a single-valued claim, which serialises as a bare string', () => {
    // The trap: .NET writes one claim as a scalar and several as an array. A future role with
    // exactly one permission would arrive as a string, and `.includes` on a string matches
    // substrings — so 'users:view' would appear to grant 'users:vie'.
    const token = tokenWith({ 'marquee:perm': 'users:view' });

    expect(decodePermissions(token)).toEqual(['users:view']);
  });

  it('returns nothing when the claim is absent', () => {
    expect(decodePermissions(tokenWith({ sub: 'someone' }))).toEqual([]);
  });

  it('survives non-ASCII payloads', () => {
    // atob yields bytes, not characters; a payload with a non-ASCII username has to be re-read as
    // UTF-8 or JSON.parse throws and every permission silently disappears.
    const token = tokenWith({ name: 'Ana Muñoz 기생충', 'marquee:perm': ['users:view'] });

    expect(decodePermissions(token)).toEqual(['users:view']);
  });

  it('degrades to no permissions rather than throwing', () => {
    // This runs inside a computed during change detection; an exception here would take the app down.
    expect(decodePermissions(null)).toEqual([]);
    expect(decodePermissions('')).toEqual([]);
    expect(decodePermissions('not-a-token')).toEqual([]);
    expect(decodePermissions('a.b.c')).toEqual([]);
  });

  it('ignores non-string entries in the claim array', () => {
    const token = tokenWith({ 'marquee:perm': ['users:view', 42, null] });

    expect(decodePermissions(token)).toEqual(['users:view']);
  });
});
