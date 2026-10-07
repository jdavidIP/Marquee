// Signs load-test accounts in the way the product does: against the user pool's own API, with the
// access token it issues. The API has no sign-up or sign-in endpoints of its own any more (#112).
//
// Local only. This talks to cognito-local (docker-compose), which accepts any confirmation code the
// stack was started with (123456) and sends no email. The real pool emails every sign-up and caps
// the account at 50 messages a day, so it cannot be used to create load-test users at all.
//
// Env: COGNITO_URL (default http://localhost:9229), COGNITO_CLIENT_ID (marquee-local-web),
//      COGNITO_CODE (123456)

const COGNITO_URL = process.env.COGNITO_URL ?? 'http://localhost:9229';
const CLIENT_ID = process.env.COGNITO_CLIENT_ID ?? 'marquee-local-web';
const CODE = process.env.COGNITO_CODE ?? '123456';

async function call(operation, body) {
  const res = await fetch(COGNITO_URL, {
    method: 'POST',
    headers: {
      'Content-Type': 'application/x-amz-json-1.1',
      'X-Amz-Target': `AWSCognitoIdentityProviderService.${operation}`,
    },
    body: JSON.stringify(body),
  });
  const text = await res.text();
  let json;
  try { json = JSON.parse(text); } catch { json = {}; }
  return { ok: res.ok, status: res.status, type: json.__type ?? '', body: json, text };
}

/** Signs an existing, confirmed account in and returns its access token. */
export async function signIn(username, password) {
  const r = await call('InitiateAuth', {
    ClientId: CLIENT_ID,
    AuthFlow: 'USER_PASSWORD_AUTH',
    AuthParameters: { USERNAME: username, PASSWORD: password },
  });
  if (!r.ok) throw new Error(`sign-in ${username} failed: ${r.status} ${r.text}`);
  return r.body.AuthenticationResult.AccessToken;
}

/**
 * Creates a confirmed account and returns its access token. Safe to re-run: a username that already
 * exists is signed in rather than refused, which is what the scripts' "run it twice" behaviour needs.
 *
 * With `api` given (e.g. http://localhost:5080/api) it also makes the account's first authenticated
 * request, which is when the API creates the user's row. Doing that here keeps the one-off row
 * creation (and its call back to the pool) out of whatever the script goes on to measure.
 */
export async function createUser(username, password, api) {
  const signUp = await call('SignUp', {
    ClientId: CLIENT_ID,
    Username: username,
    Password: password,
    UserAttributes: [{ Name: 'email', Value: `${username}@marquee.load` }],
  });
  if (!signUp.ok && signUp.type !== 'UsernameExistsException')
    throw new Error(`sign-up ${username} failed: ${signUp.status} ${signUp.text}`);

  // An already-confirmed account refuses this; that is the re-run case, not a failure.
  await call('ConfirmSignUp', { ClientId: CLIENT_ID, Username: username, ConfirmationCode: CODE });

  const token = await signIn(username, password);
  if (api) {
    const me = await fetch(`${api}/auth/me`, { headers: { Authorization: `Bearer ${token}` } });
    if (!me.ok) throw new Error(`first request for ${username} failed: ${me.status} ${await me.text()}`);
  }
  return token;
}
