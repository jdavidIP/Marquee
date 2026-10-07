import { Injectable, computed, inject, signal } from '@angular/core';
import { HttpClient, HttpErrorResponse } from '@angular/common/http';
import { Observable, catchError, map, of, switchMap, tap, throwError, timeout } from 'rxjs';
import { environment } from '../../environments/environment';
import { PasswordRulesDto, UserDto } from './models';
import { CognitoClient, CognitoError } from './cognito';

const TOKEN_KEY = 'marquee.token';
const USER_KEY = 'marquee.user';

/** Mirrors MarqueePermissions on the API. */
export const Permissions = {
  ManagePremieres: 'premieres:manage',
  ViewUsers: 'users:view',
  BlockUsers: 'users:block',
} as const;

@Injectable({ providedIn: 'root' })
export class AuthService {
  private readonly _token = signal<string | null>(localStorage.getItem(TOKEN_KEY));
  private readonly _user = signal<UserDto | null>(readStoredUser());

  readonly user = this._user.asReadonly();

  /**
   * Why the last session ended, when it ended for a reason the person should hear — the account was
   * blocked, say. The sign-in page shows it; signing in again clears it.
   */
  private readonly _notice = signal<string | null>(null);
  readonly notice = this._notice.asReadonly();
  readonly isLoggedIn = computed(() => this._token() !== null);

  /**
   * What the API says this account may do, from GET /api/auth/me — the same answer it authorises
   * against. Cognito's tokens carry no permissions (DEPLOYMENT.md § Phase 2, decision 7), so there is
   * nothing to decode. Gating on these rather than `role === 'Admin'` keeps the UI right the day
   * permissions diverge from roles.
   *
   * Only for deciding what to render: the API checks every request regardless.
   */
  private readonly permissions = computed(() => this._user()?.permissions ?? []);

  readonly canManagePremieres = computed(() => this.has(Permissions.ManagePremieres));
  readonly canViewUsers = computed(() => this.has(Permissions.ViewUsers));
  readonly canBlockUsers = computed(() => this.has(Permissions.BlockUsers));

  /** Whether to offer the operations area at all — any one of its tabs is enough. */
  readonly canSeeOperations = computed(() => this.canViewUsers() || this.canManagePremieres());

  has(permission: string): boolean {
    return this.permissions().includes(permission);
  }

  private readonly http = inject(HttpClient);
  private readonly cognito = inject(CognitoClient);

  /**
   * The credentials of a sign-up or sign-in that stopped at "confirm your email", so confirming can
   * sign straight in (decision 2's gap-closer). Memory only — never storage, never the URL — so a
   * reload loses it, and the person signs in by hand instead.
   */
  private pending: { username: string; password: string } | null = null;

  get token(): string | null {
    return this._token();
  }

  /**
   * Signs in with the user pool, then asks the API who that is. Signing in with an unconfirmed
   * account fails with `UserNotConfirmedException`; the credentials are kept so confirming the code
   * can finish the job.
   */
  signIn(username: string, password: string): Observable<UserDto> {
    return this.cognito
      .call<{ AuthenticationResult: { AccessToken: string } }>('InitiateAuth', {
        AuthFlow: 'USER_PASSWORD_AUTH',
        AuthParameters: { USERNAME: username, PASSWORD: password },
      })
      .pipe(
        catchError((err) => {
          if (err instanceof CognitoError && err.type === 'UserNotConfirmedException')
            this.pending = { username, password };
          // At sign-in a refused password can only be a wrong one. The real pool says so
          // (NotAuthorizedException); cognito-local says InvalidPasswordException, which elsewhere
          // means "breaks the policy" — so it is read as what it means here (DEPLOYMENT.md §2b).
          if (err instanceof CognitoError && err.type === 'InvalidPasswordException')
            return throwError(() => new CognitoError('NotAuthorizedException', err.message));
          return throwError(() => err);
        }),
        // The access token, not the ID token: it is the API's credential (decision 9). Stored before
        // asking for the user, so the interceptor sends it on that very request. The refresh token
        // is deliberately not kept: a session lasts as long as this token, as it always has.
        tap((r) => this.storeToken(r.AuthenticationResult.AccessToken)),
        switchMap(() => this.http.get<UserDto>(`${environment.apiBase}/auth/me`)),
        tap((user) => {
          this.storeUser(user);
          // Whatever ended the last session no longer describes this one.
          this.clearNotice();
        }),
        catchError((err) => {
          // Signed in with the pool but the API refused or failed: no half-signed-in state.
          if (!(err instanceof CognitoError)) this.logout();
          return throwError(() => err);
        }),
      );
  }

  /** Creates the account; Cognito emails a code to confirm it before it can sign in. */
  signUp(username: string, email: string, password: string): Observable<void> {
    return this.cognito
      .call('SignUp', {
        Username: username,
        Password: password,
        UserAttributes: [{ Name: 'email', Value: email }],
      })
      .pipe(
        tap(() => (this.pending = { username, password })),
        map(() => undefined),
      );
  }

  /**
   * Confirms the account with the emailed code, then signs in if this tab still holds the password
   * from signing up — `'signed-in'` — or leaves that to the person — `'confirmed'`.
   */
  confirmSignUp(username: string, code: string): Observable<'signed-in' | 'confirmed'> {
    return this.cognito.call('ConfirmSignUp', { Username: username, ConfirmationCode: code }).pipe(
      // Confirming an account that is already confirmed — a double submit, or the page reopened
      // afterwards — is NotAuthorizedException from the real pool. The account is in the state the
      // person wanted, so carry on as if this call had done it. (A disabled pool user would raise the
      // same, but Marquee never disables one — blocking lives in its own database — and signing in
      // still has to get past Cognito either way.)
      catchError((err) =>
        err instanceof CognitoError && err.type === 'NotAuthorizedException'
          ? of(undefined)
          : throwError(() => err),
      ),
      switchMap(() => {
        const pending = this.pending?.username === username ? this.pending : null;
        this.pending = null;
        return pending
          ? this.signIn(pending.username, pending.password).pipe(map(() => 'signed-in' as const))
          : of('confirmed' as const);
      }),
    );
  }

  /**
   * Re-reads the signed-in account from the API, so permissions changed since sign-in show up, and
   * signs out locally if the API no longer accepts the token — it expired (sessions last as long as
   * the 24h access token), or predates the move to Cognito. Any other failure keeps the session: a
   * network blip is not a reason to sign someone out.
   */
  refreshUser(): Observable<void> {
    return this.http.get<UserDto>(`${environment.apiBase}/auth/me`).pipe(
      // Startup waits on this, so a hung API must not hold the first screen hostage: past five
      // seconds it counts as "not a 401" — the session is kept and the app renders.
      timeout(5000),
      tap((user) => this.storeUser(user)),
      map(() => undefined),
      catchError((err: HttpErrorResponse) => {
        if (err.status === 401) this.logout();
        return of(undefined);
      }),
    );
  }

  /** Emails a fresh confirmation code. */
  resendCode(username: string): Observable<void> {
    return this.cognito.call('ResendConfirmationCode', { Username: username }).pipe(map(() => undefined));
  }

  /**
   * What a password has to satisfy, so the form can say so before anyone submits rather than after.
   * Anonymous on the API — the people who need it are the ones without an account yet.
   */
  passwordRules(): Observable<PasswordRulesDto> {
    return this.http.get<PasswordRulesDto>(`${environment.apiBase}/auth/password-rules`);
  }

  /**
   * Emails a code to reset the password. Takes the username or the email (the pool accepts either).
   * Succeeds whether or not the account exists — the pool hides which (issue #31's guarantee, now
   * kept by Cognito's user-existence protection) — so the caller moves on to the code page either way.
   */
  forgotPassword(usernameOrEmail: string): Observable<void> {
    return this.cognito.call('ForgotPassword', { Username: usernameOrEmail }).pipe(map(() => undefined));
  }

  /** Sets a new password with the emailed code. No sign-in as a side effect: the person signs in after. */
  resetPassword(usernameOrEmail: string, code: string, newPassword: string): Observable<void> {
    return this.cognito
      .call('ConfirmForgotPassword', {
        Username: usernameOrEmail,
        ConfirmationCode: code,
        Password: newPassword,
      })
      .pipe(map(() => undefined));
  }

  /** Signs out and says why, for the sign-in page to show. */
  endSession(reason: string): void {
    this.logout();
    this._notice.set(reason);
  }

  /** Called when the person acts on the notice — signing in again, say. */
  clearNotice(): void {
    this._notice.set(null);
  }

  logout(): void {
    this.pending = null;
    localStorage.removeItem(TOKEN_KEY);
    localStorage.removeItem(USER_KEY);
    this._token.set(null);
    this._user.set(null);
  }

  /**
   * Replace the cached user after they edit their own profile.
   *
   * Deliberately does not touch the token: bio and privacy are not claims, so nothing about
   * authorisation changes and reissuing would be misleading. This only keeps the copy the UI reads
   * — the topbar's name, the profile screen's own view of itself — in step with what was saved.
   */
  applyProfileUpdate(user: UserDto): void {
    this.storeUser(user);
  }

  private storeToken(token: string): void {
    localStorage.setItem(TOKEN_KEY, token);
    this._token.set(token);
  }

  private storeUser(user: UserDto): void {
    localStorage.setItem(USER_KEY, JSON.stringify(user));
    this._user.set(user);
  }
}

function readStoredUser(): UserDto | null {
  const raw = localStorage.getItem(USER_KEY);
  try {
    return raw ? (JSON.parse(raw) as UserDto) : null;
  } catch {
    return null;
  }
}
