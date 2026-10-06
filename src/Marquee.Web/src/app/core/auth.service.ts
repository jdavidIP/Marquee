import { Injectable, computed, inject, signal } from '@angular/core';
import { HttpClient } from '@angular/common/http';
import { Observable, catchError, map, of, switchMap, tap, throwError } from 'rxjs';
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
          return throwError(() => err);
        }),
        // The access token, not the ID token: it is the API's credential (decision 9). Stored before
        // asking for the user, so the interceptor sends it on that very request. The refresh token
        // is deliberately not kept: a session lasts as long as this token, as it always has.
        tap((r) => this.storeToken(r.AuthenticationResult.AccessToken)),
        switchMap(() => this.http.get<UserDto>(`${environment.apiBase}/auth/me`)),
        tap((user) => this.storeUser(user)),
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
      switchMap(() => {
        const pending = this.pending?.username === username ? this.pending : null;
        this.pending = null;
        return pending
          ? this.signIn(pending.username, pending.password).pipe(map(() => 'signed-in' as const))
          : of('confirmed' as const);
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
   * Always the same response shape whether or not the address is registered (issue #31) — the
   * caller shows whatever message comes back without branching on it, which is what actually keeps
   * that guarantee visible in the UI rather than just in the API contract.
   */
  forgotPassword(email: string): Observable<{ message: string }> {
    return this.http.post<{ message: string }>(`${environment.apiBase}/auth/forgot-password`, {
      email,
    });
  }

  /** No sign-in as a side effect, same reasoning as confirmEmail — just a message, nothing to store. */
  resetPassword(
    token: string,
    newPassword: string,
    confirmPassword: string,
  ): Observable<{ message: string }> {
    return this.http.post<{ message: string }>(`${environment.apiBase}/auth/reset-password`, {
      token,
      newPassword,
      confirmPassword,
    });
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
