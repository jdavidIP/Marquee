import { Injectable, inject, signal } from '@angular/core';
import { HttpClient } from '@angular/common/http';
import { firstValueFrom } from 'rxjs';
import { environment } from '../../environments/environment';
import { AnonymousSessionResponse } from './models';

const SESSION_KEY = 'marquee.anonSession';

interface StoredSession {
  sessionId: string;
  token: string;
  expiresAtUtc: string;
}

/**
 * Holds the short-lived session that lets a visitor clap without an account (Iteration 5).
 *
 * Requested once on first page load and kept in local storage, because a session that changed on
 * every reload would hand the same person a fresh per-participant cap each time — the cap is only
 * meaningful if the identity behind it is stable.
 */
@Injectable({ providedIn: 'root' })
export class AnonymousSessionService {
  private readonly http = inject(HttpClient);
  private readonly _session = signal<StoredSession | null>(readStored());

  readonly session = this._session.asReadonly();

  get token(): string | null {
    const current = this._session();
    return current && !isExpired(current) ? current.token : null;
  }

  /** Fetch a session if we do not hold a valid one. Safe to call on every page load. */
  async ensure(): Promise<void> {
    if (this.token) {
      return;
    }

    try {
      const issued = await firstValueFrom(
        this.http.post<AnonymousSessionResponse>(`${environment.apiBase}/sessions/anonymous`, {}),
      );
      const stored: StoredSession = {
        sessionId: issued.sessionId,
        token: issued.token,
        expiresAtUtc: issued.expiresAtUtc,
      };
      localStorage.setItem(SESSION_KEY, JSON.stringify(stored));
      this._session.set(stored);
    } catch {
      // The issuing endpoint is rate limited by IP, so this can legitimately fail. Watching a
      // Premiere does not need a session — only clapping does — so a failure here degrades the
      // page rather than breaking it.
      this._session.set(null);
    }
  }

  clear(): void {
    localStorage.removeItem(SESSION_KEY);
    this._session.set(null);
  }
}

function readStored(): StoredSession | null {
  const raw = localStorage.getItem(SESSION_KEY);
  if (!raw) {
    return null;
  }
  try {
    const parsed = JSON.parse(raw) as StoredSession;
    return isExpired(parsed) ? null : parsed;
  } catch {
    return null;
  }
}

function isExpired(session: StoredSession): boolean {
  return new Date(session.expiresAtUtc).getTime() <= Date.now();
}
