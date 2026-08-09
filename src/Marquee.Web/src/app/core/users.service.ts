import { Injectable, inject } from '@angular/core';
import { HttpClient, HttpParams } from '@angular/common/http';
import { Observable } from 'rxjs';
import { environment } from '../../environments/environment';
import { UserSearchResultDto } from './models';

/**
 * The /api/users resource. Currently search only; profile reading and self-editing are the other
 * two endpoints this service will grow into.
 */
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
}
