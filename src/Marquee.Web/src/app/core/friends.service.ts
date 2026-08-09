import { Injectable, inject } from '@angular/core';
import { HttpClient } from '@angular/common/http';
import { Observable } from 'rxjs';
import { environment } from '../../environments/environment';
import { FriendDto, FriendRequestDto } from './models';

/**
 * The signed-in user's friendships. One service per API resource, matching LibraryService and
 * PremiereService — user search lives in UsersService because it is the /api/users resource, even
 * though the friending screen is what drives it.
 *
 * There is no "withdraw a request I sent" call because the API has none: accept and reject are
 * addressee-only, and removing a friendship matches only accepted ones. Outgoing requests are
 * therefore read-only until the other person acts.
 */
@Injectable({ providedIn: 'root' })
export class FriendsService {
  private readonly http = inject(HttpClient);
  private readonly base = `${environment.apiBase}/friends`;

  friends(): Observable<FriendDto[]> {
    return this.http.get<FriendDto[]>(this.base);
  }

  /** Pending requests in both directions; each carries `outgoing` saying which way it points. */
  requests(): Observable<FriendRequestDto[]> {
    return this.http.get<FriendRequestDto[]>(`${this.base}/requests`);
  }

  /**
   * By username rather than id, mirroring the endpoint. Note the server resolves the reciprocal
   * case itself: asking someone who has already asked you accepts their request instead of opening
   * a second one, so a caller must refresh both lists rather than assume a new pending request.
   */
  sendRequest(username: string): Observable<FriendRequestDto> {
    return this.http.post<FriendRequestDto>(`${this.base}/requests`, { username });
  }

  accept(requestId: string): Observable<FriendRequestDto> {
    return this.http.post<FriendRequestDto>(`${this.base}/requests/${requestId}/accept`, {});
  }

  reject(requestId: string): Observable<FriendRequestDto> {
    return this.http.post<FriendRequestDto>(`${this.base}/requests/${requestId}/reject`, {});
  }

  /** Takes the other user's id, not a friendship id — the route is /api/friends/{userId}. */
  remove(userId: string): Observable<void> {
    return this.http.delete<void>(`${this.base}/${userId}`);
  }
}
