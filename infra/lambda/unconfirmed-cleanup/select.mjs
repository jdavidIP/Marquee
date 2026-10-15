// The cleanup's rules, kept free of the AWS SDK so they can be tested anywhere (#30).

const DAY_MS = 24 * 60 * 60 * 1000;

/**
 * Users to delete: UNCONFIRMED and created strictly more than minAgeDays ago. ListUsers already
 * filters on status server-side; checking it again here means a mistyped filter can never widen
 * deletion to confirmed users. A record without a creation date is never selected.
 */
export function selectExpired(users, now, minAgeDays) {
  const cutoff = now.getTime() - minAgeDays * DAY_MS;
  return users.filter(
    (u) => u.UserStatus === 'UNCONFIRMED'
      && u.UserCreateDate instanceof Date
      && u.UserCreateDate.getTime() < cutoff,
  );
}

/**
 * Deleting takes exactly {"dryRun": false}, which is what the schedule sends. Anything else — an
 * empty event, the console's sample event, a misspelled key or value — is a dry run, so a mistake
 * can never delete anyone.
 */
export function isDryRun(event) {
  return event?.dryRun !== false;
}

/** The window from the environment; anything but a positive integer is a broken deploy. */
export function parseMinAgeDays(raw) {
  const days = Number(raw);
  if (raw === undefined || raw === '' || !Number.isInteger(days) || days < 1) {
    throw new Error(`MIN_AGE_DAYS must be a positive integer, got ${JSON.stringify(raw)}`);
  }
  return days;
}
