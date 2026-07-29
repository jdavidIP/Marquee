import { Injectable } from '@angular/core';
import { HttpClient } from '@angular/common/http';
import { Observable } from 'rxjs';
import { environment } from '../../environments/environment';
import { ClapResponse, FriendContributorsResponse, PremiereDto } from './models';

@Injectable({ providedIn: 'root' })
export class PremiereService {
  constructor(private http: HttpClient) {}

  getActive(): Observable<PremiereDto> {
    return this.http.get<PremiereDto>(`${environment.apiBase}/premieres/active`);
  }

  get(id: string): Observable<PremiereDto> {
    return this.http.get<PremiereDto>(`${environment.apiBase}/premieres/${id}`);
  }

  /** The next Premiere the scheduler has lined up, for the "come back at…" state. */
  getNext(): Observable<PremiereDto> {
    return this.http.get<PremiereDto>(`${environment.apiBase}/premieres/next`);
  }

  /**
   * Clap once. Each call carries a fresh Idempotency-Key so that if the response is lost to a flaky
   * connection, a retry of *that* request replays the original outcome instead of counting a second
   * clap. The key belongs to the request, not to the tap — a genuine second tap is a new clap and
   * gets a new key.
   */
  clap(id: string): Observable<ClapResponse> {
    return this.http.post<ClapResponse>(
      `${environment.apiBase}/premieres/${id}/clap`,
      {},
      { headers: { 'Idempotency-Key': crypto.randomUUID() } },
    );
  }

  /** Which of my friends clapped for this Premiere — asked per viewer, never broadcast. */
  friendContributors(id: string): Observable<FriendContributorsResponse> {
    return this.http.get<FriendContributorsResponse>(
      `${environment.apiBase}/premieres/${id}/friends`,
    );
  }

  /** Admin-only: manually create (and immediately activate) a Premiere. */
  create(): Observable<PremiereDto> {
    return this.http.post<PremiereDto>(`${environment.apiBase}/premieres`, {});
  }
}
