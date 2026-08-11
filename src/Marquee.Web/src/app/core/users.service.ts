import { Injectable, inject } from '@angular/core';
import { HttpClient, HttpParams } from '@angular/common/http';
import { Observable } from 'rxjs';
import { environment } from '../../environments/environment';
import { FriendDto, ProfileDto, UpdateProfileRequest, UserDto, UserSearchResultDto } from './models';

/** The /api/users resource: search, profile reading, and editing your own. */
@Injectable({ providedIn: 'root' })
export class UsersService {
  private readonly http = inject(HttpClient);
  private readonly base = `${environment.apiBase}/users`;

  /**
   * Find users by username prefix. Private accounts appear in the results like any other — privacy
   * restricts detail, not existence — and the server returns the same two fields either way, so
   * there is nothing for the client to filter or redact.
   */
  search(query: string, limit = 10): Observable<UserSearchResultDto[]> {
    const params = new HttpParams().set('query', query).set('limit', limit);
    return this.http.get<UserSearchResultDto[]>(this.base, { params });
  }

  /**
   * One username, two possible shapes. The server decides which by who is asking — see
   * isFullProfile in models. The caller must not infer entitlement from isPrivate.
   */
  profile(username: string): Observable<ProfileDto> {
    return this.http.get<ProfileDto>(`${this.base}/${encodeURIComponent(username)}`);
  }

  /**
   * Update your own bio or privacy. Scoped to the caller by construction — there is no user id in
   * the route, so this cannot be aimed at somebody else's account. Returns the updated user, which
   * the caller should hand to AuthService so the stored copy does not go stale.
   */
  updateMe(request: UpdateProfileRequest): Observable<UserDto> {
    return this.http.patch<UserDto>(`${this.base}/me`, request);
  }

  /**
   * Someone else's friend list, visible to the same audience as the rest of their profile. Unlike
   * `profile()`, a denied viewer gets a 403 here rather than a smaller 200 — see the API's own doc
   * comment on why a reduced-but-200 response would be indistinguishable from "genuinely no friends".
   */
  friendsOf(username: string, search = ''): Observable<FriendDto[]> {
    const params = search.trim() ? { params: { search: search.trim() } } : {};
    return this.http.get<FriendDto[]>(`${this.base}/${encodeURIComponent(username)}/friends`, params);
  }
}
