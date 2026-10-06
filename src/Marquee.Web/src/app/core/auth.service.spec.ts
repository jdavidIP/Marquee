import { TestBed, fakeAsync, tick } from '@angular/core/testing';
import { Router, provideRouter } from '@angular/router';
import { HttpClient, provideHttpClient, withInterceptors } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { AuthService } from './auth.service';
import { CognitoError } from './cognito';
import { authInterceptor } from './auth.interceptor';
import { environment } from '../../environments/environment';
import { UserDto } from './models';

/**
 * Phase 2 (#111): the browser talks to the Cognito user pool directly and to the API for who the
 * account is. These drive both through HttpTestingController, so what is asserted is the requests
 * actually made — operation, body, headers — not a mock's idea of them.
 */
describe('AuthService', () => {
  let auth: AuthService;
  let http: HttpTestingController;

  const admin: UserDto = {
    id: 'u-1',
    username: 'ana',
    email: 'ana@marquee.test',
    bio: null,
    isPrivate: false,
    role: 'Admin',
    avatarUrl: null,
    permissions: ['premieres:manage', 'users:view'],
  };

  beforeEach(() => {
    localStorage.clear();
    TestBed.configureTestingModule({
      providers: [
        provideRouter([]),
        provideHttpClient(withInterceptors([authInterceptor])),
        provideHttpClientTesting(),
      ],
    });
    auth = TestBed.inject(AuthService);
    http = TestBed.inject(HttpTestingController);
  });

  afterEach(() => {
    http.verify();
    localStorage.clear();
  });

  /** The next request to the pool, checked for the operation it names. */
  function expectCognito(operation: string) {
    const req = http.expectOne(environment.cognito.endpoint);
    expect(req.request.headers.get('X-Amz-Target')).toBe(`AWSCognitoIdentityProviderService.${operation}`);
    expect(req.request.headers.get('Content-Type')).toBe('application/x-amz-json-1.1');
    expect(req.request.body.ClientId).toBe(environment.cognito.clientId);
    return req;
  }

  function refuse(req: ReturnType<typeof expectCognito>, type: string): void {
    req.flush({ __type: type, message: type }, { status: 400, statusText: 'Bad Request' });
  }

  it('signs in with the pool, then takes the account and its permissions from the API', () => {
    let signedIn: UserDto | undefined;
    auth.signIn('ana', 'pw-1234567890').subscribe((u) => (signedIn = u));

    const initiate = expectCognito('InitiateAuth');
    expect(initiate.request.body).toEqual(
      jasmine.objectContaining({
        AuthFlow: 'USER_PASSWORD_AUTH',
        AuthParameters: { USERNAME: 'ana', PASSWORD: 'pw-1234567890' },
      }),
    );
    initiate.flush({ AuthenticationResult: { AccessToken: 'access', IdToken: 'id', RefreshToken: 'refresh' } });

    // The access token — not the ID token — is what the API is sent.
    const me = http.expectOne(`${environment.apiBase}/auth/me`);
    expect(me.request.headers.get('Authorization')).toBe('Bearer access');
    me.flush(admin);

    expect(signedIn).toEqual(admin);
    expect(auth.token).toBe('access');
    expect(auth.canManagePremieres()).toBeTrue();
    expect(auth.canBlockUsers()).toBeFalse();
    // A 30-day credential stays out of storage: the session lasts as long as the access token.
    expect(JSON.stringify(localStorage)).not.toContain('refresh');
  });

  it('never sends our credentials to the pool', () => {
    localStorage.setItem('marquee.token', 'access');
    TestBed.resetTestingModule();
    TestBed.configureTestingModule({
      providers: [
        provideRouter([]),
        provideHttpClient(withInterceptors([authInterceptor])),
        provideHttpClientTesting(),
      ],
    });
    http = TestBed.inject(HttpTestingController);

    TestBed.inject(AuthService).resendCode('ana').subscribe();

    const req = expectCognito('ResendConfirmationCode');
    expect(req.request.headers.has('Authorization')).toBeFalse();
    expect(req.request.headers.has('X-Anon-Session')).toBeFalse();
    req.flush({});

    // ...while our own API still gets them.
    TestBed.inject(HttpClient).get(`${environment.apiBase}/friends`).subscribe();
    expect(http.expectOne(`${environment.apiBase}/friends`).request.headers.get('Authorization')).toBe(
      'Bearer access',
    );
  });

  it('turns a pool refusal into a typed error', () => {
    let error: unknown;
    auth.signIn('ana', 'wrong').subscribe({ error: (e) => (error = e) });

    refuse(expectCognito('InitiateAuth'), 'NotAuthorizedException');

    expect(error).toEqual(jasmine.any(CognitoError));
    expect((error as CognitoError).type).toBe('NotAuthorizedException');
    expect(auth.isLoggedIn()).toBeFalse();
  });

  it('reads a password refused at sign-in as a wrong password, whatever the pool calls it', () => {
    // cognito-local answers a wrong password with InvalidPasswordException, which would otherwise
    // be shown as "breaks the password policy".
    let error: CognitoError | undefined;
    auth.signIn('ana', 'wrong-password-1').subscribe({ error: (e) => (error = e) });

    refuse(expectCognito('InitiateAuth'), 'InvalidPasswordException');

    expect(error!.type).toBe('NotAuthorizedException');
  });

  it('reads a namespaced error type by its last part', () => {
    let error: CognitoError | undefined;
    auth.resendCode('ana').subscribe({ error: (e) => (error = e) });

    refuse(expectCognito('ResendConfirmationCode'), 'CognitoLocal#Unsupported');

    expect(error!.type).toBe('Unsupported');
  });

  it('signs in straight after confirming the code it signed up for', () => {
    auth.signUp('ana', 'ana@marquee.test', 'pw-1234567890').subscribe();
    const signUp = expectCognito('SignUp');
    expect(signUp.request.body).toEqual(
      jasmine.objectContaining({
        Username: 'ana',
        Password: 'pw-1234567890',
        UserAttributes: [{ Name: 'email', Value: 'ana@marquee.test' }],
      }),
    );
    signUp.flush({ UserConfirmed: false });

    let outcome: string | undefined;
    auth.confirmSignUp('ana', '123456').subscribe((o) => (outcome = o));
    expectCognito('ConfirmSignUp').flush({});

    const initiate = expectCognito('InitiateAuth');
    expect(initiate.request.body.AuthParameters).toEqual({ USERNAME: 'ana', PASSWORD: 'pw-1234567890' });
    initiate.flush({ AuthenticationResult: { AccessToken: 'access' } });
    http.expectOne(`${environment.apiBase}/auth/me`).flush(admin);

    expect(outcome).toBe('signed-in');
  });

  it('finishes an unconfirmed sign-in once the code is entered', () => {
    auth.signIn('ana', 'pw-1234567890').subscribe({ error: () => {} });
    refuse(expectCognito('InitiateAuth'), 'UserNotConfirmedException');

    auth.confirmSignUp('ana', '123456').subscribe();
    expectCognito('ConfirmSignUp').flush({});

    expectCognito('InitiateAuth').flush({ AuthenticationResult: { AccessToken: 'access' } });
    http.expectOne(`${environment.apiBase}/auth/me`).flush(admin);
    expect(auth.isLoggedIn()).toBeTrue();
  });

  it('treats confirming an already-confirmed account as done, and still signs in', () => {
    // A double submit, or the code page reopened after confirming: the real pool answers
    // NotAuthorizedException ("Current status is CONFIRMED").
    auth.signUp('ana', 'ana@marquee.test', 'pw-1234567890').subscribe();
    expectCognito('SignUp').flush({ UserConfirmed: false });

    let outcome: string | undefined;
    auth.confirmSignUp('ana', '123456').subscribe((o) => (outcome = o));
    refuse(expectCognito('ConfirmSignUp'), 'NotAuthorizedException');

    expectCognito('InitiateAuth').flush({ AuthenticationResult: { AccessToken: 'access' } });
    http.expectOne(`${environment.apiBase}/auth/me`).flush(admin);
    expect(outcome).toBe('signed-in');
  });

  it('leaves signing in to the person when it holds no password for that account', () => {
    let outcome: string | undefined;
    auth.confirmSignUp('someone-else', '123456').subscribe((o) => (outcome = o));
    expectCognito('ConfirmSignUp').flush({});

    expect(outcome).toBe('confirmed');
    expect(auth.isLoggedIn()).toBeFalse();
  });

  /** Signs in as `admin` (via the pool and /me), leaving no request outstanding. */
  function signedIn(): void {
    auth.signIn('ana', 'pw-1234567890').subscribe();
    expectCognito('InitiateAuth').flush({ AuthenticationResult: { AccessToken: 'access' } });
    http.expectOne(`${environment.apiBase}/auth/me`).flush(admin);
  }

  it('refreshes the account, so a permission changed since sign-in shows up', () => {
    signedIn();
    expect(auth.canManagePremieres()).toBeTrue();

    auth.refreshUser().subscribe();
    http.expectOne(`${environment.apiBase}/auth/me`).flush({ ...admin, permissions: [] });

    expect(auth.canManagePremieres()).toBeFalse();
    expect(auth.isLoggedIn()).toBeTrue();
  });

  it('signs out, and becomes a visitor, when the API stops accepting the token', () => {
    // An expired 24h access token, or one from before the move to Cognito.
    signedIn();

    TestBed.inject(HttpClient).get(`${environment.apiBase}/friends`).subscribe({ error: () => {} });
    http
      .expectOne(`${environment.apiBase}/friends`)
      .flush(null, { status: 401, statusText: 'Unauthorized' });

    expect(auth.isLoggedIn()).toBeFalse();
    // A visitor session, so clapping still works.
    http.expectOne(`${environment.apiBase}/sessions/anonymous`).flush({
      sessionId: 's',
      token: 'anon',
      expiresAtUtc: new Date(Date.now() + 3_600_000).toISOString(),
    });
  });

  it('signs a refused account out, says why, and goes to sign-in', () => {
    signedIn();
    const navigate = spyOn(TestBed.inject(Router), 'navigate');

    TestBed.inject(HttpClient).get(`${environment.apiBase}/friends`).subscribe({ error: () => {} });
    http
      .expectOne(`${environment.apiBase}/friends`)
      .flush(
        { error: 'This account has been blocked.', code: 'account_blocked' },
        { status: 403, statusText: 'Forbidden' },
      );

    expect(auth.isLoggedIn()).toBeFalse();
    expect(auth.notice()).toBe('This account has been blocked.');
    expect(navigate).toHaveBeenCalledWith(['/login']);
    http.expectOne(`${environment.apiBase}/sessions/anonymous`).flush({
      sessionId: 's',
      token: 'anon',
      expiresAtUtc: new Date(Date.now() + 3_600_000).toISOString(),
    });
  });

  it('stays signed in on an ordinary 403 for a permission the account lacks', () => {
    signedIn();

    TestBed.inject(HttpClient).get(`${environment.apiBase}/admin/users`).subscribe({ error: () => {} });
    http
      .expectOne(`${environment.apiBase}/admin/users`)
      .flush({ error: 'Forbidden' }, { status: 403, statusText: 'Forbidden' });

    expect(auth.isLoggedIn()).toBeTrue();
    expect(auth.notice()).toBeNull();
  });

  it('gives up on a hung API after five seconds and keeps the session', fakeAsync(() => {
    signedIn();
    let done = false;

    auth.refreshUser().subscribe(() => (done = true));
    http.expectOne(`${environment.apiBase}/auth/me`);
    tick(5000);

    expect(done).toBeTrue();
    expect(auth.isLoggedIn()).toBeTrue();
  }));

  it('keeps the session through a failure that is not about the token', () => {
    signedIn();

    auth.refreshUser().subscribe();
    http
      .expectOne(`${environment.apiBase}/auth/me`)
      .flush(null, { status: 503, statusText: 'Service Unavailable' });

    expect(auth.isLoggedIn()).toBeTrue();
  });

  it('does not stay half signed in when the API refuses the account', () => {
    let error: unknown;
    auth.signIn('ana', 'pw-1234567890').subscribe({ error: (e) => (error = e) });
    expectCognito('InitiateAuth').flush({ AuthenticationResult: { AccessToken: 'access' } });

    http
      .expectOne(`${environment.apiBase}/auth/me`)
      .flush({ error: 'This account has been blocked.' }, { status: 403, statusText: 'Forbidden' });

    expect(error).toBeTruthy();
    expect(auth.isLoggedIn()).toBeFalse();
    expect(localStorage.getItem('marquee.token')).toBeNull();
  });
});
