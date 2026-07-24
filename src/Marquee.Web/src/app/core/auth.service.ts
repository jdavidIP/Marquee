import { Injectable, computed, signal } from '@angular/core';
import { HttpClient } from '@angular/common/http';
import { Observable, tap } from 'rxjs';
import { environment } from '../../environments/environment';
import { AuthResponse, UserDto } from './models';

const TOKEN_KEY = 'marquee.token';
const USER_KEY = 'marquee.user';

@Injectable({ providedIn: 'root' })
export class AuthService {
  private readonly _token = signal<string | null>(localStorage.getItem(TOKEN_KEY));
  private readonly _user = signal<UserDto | null>(readStoredUser());

  readonly user = this._user.asReadonly();
  readonly isLoggedIn = computed(() => this._token() !== null);
  readonly isAdmin = computed(() => this._user()?.role === 'Admin');

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
