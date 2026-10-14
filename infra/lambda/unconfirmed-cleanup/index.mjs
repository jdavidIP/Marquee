// Deletes Cognito users who never confirmed their email, once they are older than MIN_AGE_DAYS
// (#30). Invoked daily by EventBridge Scheduler with {}; invoke it from the console with
// {"dryRun": true} to see what it would delete without deleting anything.
import {
  CognitoIdentityProviderClient,
  ListUsersCommand,
  AdminDeleteUserCommand,
} from '@aws-sdk/client-cognito-identity-provider';
import { selectExpired, isDryRun, parseMinAgeDays } from './select.mjs';

const cognito = new CognitoIdentityProviderClient({});

export async function handler(event) {
  const dryRun = isDryRun(event);
  const userPoolId = process.env.USER_POOL_ID;
  if (!userPoolId) throw new Error('USER_POOL_ID is not set');
  const minAgeDays = parseMinAgeDays(process.env.MIN_AGE_DAYS);

  // Collect every page before deleting anything: deleting while paginating can shift the pages
  // underneath and silently skip users.
  const users = [];
  let paginationToken;
  do {
    const page = await cognito.send(new ListUsersCommand({
      UserPoolId: userPoolId,
      Filter: 'cognito:user_status = "UNCONFIRMED"',
      Limit: 60,
      PaginationToken: paginationToken,
    }));
    users.push(...(page.Users ?? []));
    paginationToken = page.PaginationToken;
  } while (paginationToken);

  const selected = selectExpired(users, new Date(), minAgeDays);
  const summary = {
    dryRun,
    scanned: users.length,
    // Usernames only: emails stay out of the logs.
    selected: selected.map((u) => ({ username: u.Username, createdAt: u.UserCreateDate.toISOString() })),
    deleted: 0,
    failed: [],
  };

  if (!dryRun) {
    for (const user of selected) {
      try {
        await cognito.send(new AdminDeleteUserCommand({ UserPoolId: userPoolId, Username: user.Username }));
        summary.deleted++;
      } catch (err) {
        // Confirmed or deleted since the listing: the goal is met, and a retried run stays harmless.
        if (err.name === 'UserNotFoundException') {
          summary.deleted++;
        } else {
          summary.failed.push({ username: user.Username, error: err.name });
        }
      }
    }
  }

  console.log(JSON.stringify(summary));
  // Carry on past one bad user, but still count the run as an error so the alarm fires.
  if (summary.failed.length > 0) {
    throw new Error(`${summary.failed.length} deletion(s) failed; see the summary above`);
  }
  return summary;
}
