// k6 counterpart of cognito.mjs: signs load-test accounts in against the user pool's own API, with
// the access token it issues. The API has no sign-up or sign-in endpoints of its own any more (#112).
//
// Local only — see cognito.mjs for why this cannot be pointed at the real pool.
//
// Env: COGNITO_URL (default http://localhost:9229), COGNITO_CLIENT_ID (marquee-local-web),
//      COGNITO_CODE (123456)

import http from 'k6/http';

const COGNITO_URL = __ENV.COGNITO_URL || 'http://localhost:9229';
const CLIENT_ID = __ENV.COGNITO_CLIENT_ID || 'marquee-local-web';
const CODE = __ENV.COGNITO_CODE || '123456';

function request(operation, body) {
  return [
    'POST',
    COGNITO_URL,
    JSON.stringify(body),
    {
      headers: {
        'Content-Type': 'application/x-amz-json-1.1',
        'X-Amz-Target': `AWSCognitoIdentityProviderService.${operation}`,
      },
    },
  ];
}

function signInRequest(username, password) {
  return request('InitiateAuth', {
    ClientId: CLIENT_ID,
    AuthFlow: 'USER_PASSWORD_AUTH',
    AuthParameters: { USERNAME: username, PASSWORD: password },
  });
}

function accessToken(username, response) {
  if (response.status !== 200) throw new Error(`sign-in ${username} failed: ${response.status} ${response.body}`);
  return response.json('AuthenticationResult.AccessToken');
}

/** Signs an existing, confirmed account in and returns its access token. */
export function signIn(username, password) {
  return accessToken(username, http.request(...signInRequest(username, password)));
}

/**
 * Creates confirmed accounts and returns their access tokens, in the order given. Each step is sent
 * for every user at once (http.batch) because setup() has a time limit and one sequential round trip
 * per user per step would spend it. Safe to re-run: sign-up and confirm refusing an account that
 * already exists is the re-run case, and only the sign-in has to succeed.
 *
 * With `api` given (e.g. http://localhost:5080/api) it also makes each account's first authenticated
 * request, which is when the API creates the user's row — so that one-off cost (and its call back to
 * the pool) stays out of whatever the script goes on to measure.
 */
export function createUsers(usernames, password, api) {
  http.batch(usernames.map((u) => request('SignUp', {
    ClientId: CLIENT_ID,
    Username: u,
    Password: password,
    UserAttributes: [{ Name: 'email', Value: `${u}@marquee.load` }],
  })));
  http.batch(usernames.map((u) => request('ConfirmSignUp', {
    ClientId: CLIENT_ID,
    Username: u,
    ConfirmationCode: CODE,
  })));

  const signIns = http.batch(usernames.map((u) => signInRequest(u, password)));
  const tokens = signIns.map((r, i) => accessToken(usernames[i], r));

  if (api) {
    const firstRequests = http.batch(tokens.map((t) => [
      'GET', `${api}/auth/me`, null, { headers: { Authorization: `Bearer ${t}` } },
    ]));
    firstRequests.forEach((r, i) => {
      if (r.status !== 200) throw new Error(`first request for ${usernames[i]} failed: ${r.status} ${r.body}`);
    });
  }
  return tokens;
}
