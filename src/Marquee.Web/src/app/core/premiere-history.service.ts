import { Injectable, inject } from '@angular/core';
import { HttpClient, HttpParams } from '@angular/common/http';
import { Observable } from 'rxjs';
import { environment } from '../../environments/environment';
import { PagedResult, PremiereHistoryEntryDto, PremiereHistoryQuery } from './models';

/**
 * The /api/users/{username}/premieres resource (issue #38) — which Premieres a user contributed to
 * and what they earned each time. There is no "mine" shortcut the way LibraryService has one: a
 * viewer reaches their own history through the same route as anyone else's, `/u/:username/premieres`
 * with their own username, since the API entitles self the same way it entitles a friend.
 */
@Injectable({ providedIn: 'root' })
export class PremiereHistoryService {
  private readonly http = inject(HttpClient);

  forUser(
    username: string,
    query: PremiereHistoryQuery = {},
  ): Observable<PagedResult<PremiereHistoryEntryDto>> {
    return this.http.get<PagedResult<PremiereHistoryEntryDto>>(
      `${environment.apiBase}/users/${encodeURIComponent(username)}/premieres`,
      { params: toParams(query) },
    );
  }
}

function toParams(query: PremiereHistoryQuery): HttpParams {
  let params = new HttpParams();

  for (const [key, value] of Object.entries(query)) {
    if (value === null || value === undefined) continue;
    if (typeof value === 'string' && value.trim() === '') continue;
    params = params.set(key, String(value));
  }

  return params;
}
