import { apiError, isCooldownConflict } from './http-error';

describe('apiError', () => {
  it("prefers the server's own message", () => {
    // The whole point: the API writes these to be read, so "This Premiere is already running."
    // must reach the screen instead of a generic fallback.
    const err = { status: 409, error: { error: 'This Premiere is already running.' } };

    expect(apiError(err, 'fallback')).toBe('This Premiere is already running.');
  });

  it("prefers the server's message even on a status with its own wording", () => {
    const err = { status: 404, error: { error: 'No such Premiere in this scope.' } };

    expect(apiError(err, 'fallback')).toBe('No such Premiere in this scope.');
  });

  it('explains an unreachable API', () => {
    expect(apiError({ status: 0 }, 'fallback')).toContain('Cannot reach the server');
  });

  it('explains an expired session and a missing permission distinctly', () => {
    expect(apiError({ status: 401 }, 'fallback')).toContain('session has expired');
    expect(apiError({ status: 403 }, 'fallback')).toContain('do not have permission');
  });

  it('falls back when there is nothing better to say', () => {
    expect(apiError({ status: 500 }, 'Could not load users.')).toBe('Could not load users.');
    expect(apiError(undefined, 'Could not load users.')).toBe('Could not load users.');
  });
});

describe('isCooldownConflict', () => {
  it('recognises an overridable cooldown refusal', () => {
    const err = { status: 409, error: { error: 'That film premiered on…', cooldown: true } };

    expect(isCooldownConflict(err)).toBe(true);
  });

  it('does not mistake other conflicts for it', () => {
    // An already-queued film is a 409 too, but has no override — offering one would be a lie.
    expect(isCooldownConflict({ status: 409, error: { error: 'already lined up' } })).toBe(false);
    expect(isCooldownConflict({ status: 400, error: { cooldown: true } })).toBe(false);
  });
});
