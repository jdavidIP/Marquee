import { Injectable, computed, signal } from '@angular/core';
import { HttpClient } from '@angular/common/http';
import { Observable, tap } from 'rxjs';
import { environment } from '../../environments/environment';
import { AuthResponse, PasswordRulesDto, UserDto } from './models';
import { decodePermissions } from './jwt';

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
   * Derived from the token rather than the stored role, matching how the API decides. The backend
   * gates on permission claims precisely so permissions can diverge from roles later; gating the UI
   * on `role === 'Admin'` would re-couple them and go stale the moment they do.
   *
   * Recomputes on sign-in and sign-out for free, because it reads the same signal the token lives in.
   */
  private readonly permissions = computed(() => decodePermissions(this._token()));

  readonly canManagePremieres = computed(() => this.has(Permissions.ManagePremieres));
  readonly canViewUsers = computed(() => this.has(Permissions.ViewUsers));
  readonly canBlockUsers = computed(() => this.has(Permissions.BlockUsers));

  /** Whether to offer the operations area at all — any one of its tabs is enough. */
  readonly canSeeOperations = computed(() => this.canViewUsers() || this.canManagePremieres());

  has(permission: string): boolean {
    return this.permissions().includes(permission);
  }

  constructor(private http: HttpClient) {}

  get token(): string | null {
    return this._token();
  }

  register(
    username: string,
    email: string,
    password: string,
    confirmPassword: string,
  ): Observable<AuthResponse> {
    return this.http
      .post<AuthResponse>(`${environment.apiBase}/auth/register`, {
        username,
        email,
        password,
        confirmPassword,
      })
      .pipe(tap((r) => this.store(r)));
  }

  /**
   * What a password has to satisfy, so the form can say so before anyone submits rather than after.
   * Anonymous on the API — the people who need it are the ones without an account yet.
   */
  passwordRules(): Observable<PasswordRulesDto> {
    return this.http.get<PasswordRulesDto>(`${environment.apiBase}/auth/password-rules`);
  }

  /**
   * Confirms the account the token names. No sign-in as a side effect (issue #48) — the response is
   * just a message, so this deliberately does not pipe through `store()` the way register/login do.
   */
  confirmEmail(token: string): Observable<{ message: string }> {
    return this.http.get<{ message: string }>(`${environment.apiBase}/auth/confirm-email`, {
      params: { token },
    });
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

  login(usernameOrEmail: string, password: string): Observable<AuthResponse> {
    return this.http
      .post<AuthResponse>(`${environment.apiBase}/auth/login`, { usernameOrEmail, password })
      .pipe(tap((r) => this.store(r)));
  }

  logout(): void {
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
    localStorage.setItem(USER_KEY, JSON.stringify(user));
    this._user.set(user);
  }

  private store(r: AuthResponse): void {
    localStorage.setItem(TOKEN_KEY, r.token);
    localStorage.setItem(USER_KEY, JSON.stringify(r.user));
    this._token.set(r.token);
    this._user.set(r.user);
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
