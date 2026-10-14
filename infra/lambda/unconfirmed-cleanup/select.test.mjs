import { test } from 'node:test';
import assert from 'node:assert/strict';
import { selectExpired, isDryRun, parseMinAgeDays } from './select.mjs';

const DAY = 24 * 60 * 60 * 1000;
const NOW = new Date('2026-10-21T12:00:00Z');
const user = (name, status, ageMs) => ({
  Username: name,
  UserStatus: status,
  UserCreateDate: new Date(NOW.getTime() - ageMs),
});
const names = (users) => users.map((u) => u.Username);

test('an unconfirmed user older than the window is selected', () => {
  assert.deepEqual(names(selectExpired([user('old', 'UNCONFIRMED', 8 * DAY)], NOW, 7)), ['old']);
});

test('an unconfirmed user newer than the window is not', () => {
  assert.deepEqual(selectExpired([user('new', 'UNCONFIRMED', 6 * DAY)], NOW, 7), []);
});

test('a user exactly at the window is not selected yet', () => {
  assert.deepEqual(selectExpired([user('edge', 'UNCONFIRMED', 7 * DAY)], NOW, 7), []);
  assert.deepEqual(names(selectExpired([user('edge+1', 'UNCONFIRMED', 7 * DAY + 1)], NOW, 7)), ['edge+1']);
});

test('no other status is ever selected, however old', () => {
  const ancient = 365 * DAY;
  const others = ['CONFIRMED', 'FORCE_CHANGE_PASSWORD', 'RESET_REQUIRED', 'EXTERNAL_PROVIDER', 'UNKNOWN']
    .map((s) => user(s, s, ancient));
  assert.deepEqual(selectExpired(others, NOW, 7), []);
});

test('a user with no creation date is never selected', () => {
  assert.deepEqual(selectExpired([{ Username: 'nodate', UserStatus: 'UNCONFIRMED' }], NOW, 7), []);
});

test('an empty list selects nothing', () => {
  assert.deepEqual(selectExpired([], NOW, 7), []);
});

test('only a payload naming dryRun means a dry run', () => {
  assert.equal(isDryRun({}), false);
  assert.equal(isDryRun(undefined), false);
  assert.equal(isDryRun(null), false);
  assert.equal(isDryRun({ dryRun: false }), false);
  assert.equal(isDryRun({ dryRun: true }), true);
  // Anything else a person might type still errs on the side of not deleting.
  assert.equal(isDryRun({ dryRun: 'true' }), true);
  assert.equal(isDryRun({ dryRun: 'false' }), true);
  assert.equal(isDryRun({ dryRun: 1 }), true);
});

test('MIN_AGE_DAYS must be a positive integer', () => {
  assert.equal(parseMinAgeDays('7'), 7);
  for (const bad of [undefined, '', '0', '-1', '1.5', 'seven']) {
    assert.throws(() => parseMinAgeDays(bad), /MIN_AGE_DAYS/, `accepted ${JSON.stringify(bad)}`);
  }
});
