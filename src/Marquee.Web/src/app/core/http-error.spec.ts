import { apiError, isCooldownConflict, passwordProblems } from './http-error';

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

describe('passwordProblems', () => {
  it('lifts the itemised rules out of a rejected registration', () => {
    const err = {
      status: 400,
      error: {
        error: 'Use at least 12 characters. Include at least one number.',
        problems: [
          { rule: 'TooShort', message: 'Use at least 12 characters.' },
          { rule: 'NoDigit', message: 'Include at least one number.' },
        ],
      },
    };

    expect(passwordProblems(err).map((p) => p.rule)).toEqual(['TooShort', 'NoDigit']);
  });

  it('returns nothing for a failure that carries no rules', () => {
    // A 409 on a taken username is a 400-shaped screen but not a policy refusal, and a 400 from
    // model-state validation has no `problems` at all — both must fall back to the plain sentence.
    expect(passwordProblems({ status: 409, error: { error: 'Username is taken.' } })).toEqual([]);
    expect(passwordProblems({ status: 400, error: { errors: { Password: ['required'] } } })).toEqual([]);
    expect(passwordProblems(undefined)).toEqual([]);
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
