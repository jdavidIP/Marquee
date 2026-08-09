import { Injectable, computed, signal } from '@angular/core';
import { HttpClient } from '@angular/common/http';
import { Observable, tap } from 'rxjs';
import { environment } from '../../environments/environment';
import { AuthResponse, UserDto } from './models';
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

  register(username: string, email: string, password: string): Observable<AuthResponse> {
    return this.http
      .post<AuthResponse>(`${environment.apiBase}/auth/register`, { username, email, password })
      .pipe(tap((r) => this.store(r)));
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
