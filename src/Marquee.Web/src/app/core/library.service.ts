import { Injectable } from '@angular/core';
import { HttpClient, HttpParams } from '@angular/common/http';
import { Observable } from 'rxjs';
import { environment } from '../../environments/environment';
import { LibraryFiltersDto, LibraryQuery, LibraryPageDto } from './models';

@Injectable({ providedIn: 'root' })
export class LibraryService {
  constructor(private http: HttpClient) {}

  mine(query: LibraryQuery = {}): Observable<LibraryPageDto> {
    return this.http.get<LibraryPageDto>(`${environment.apiBase}/library`, {
      params: toParams(query),
    });
  }

  filters(): Observable<LibraryFiltersDto> {
    return this.http.get<LibraryFiltersDto>(`${environment.apiBase}/library/filters`);
  }

  /**
   * Someone else's library, same query shape and response shape as `mine()` (issue #38 reuses
   * #26's querying rather than duplicating it) — the header stats describe the account being
   * viewed, not the viewer, so anyone entitled to see the entries sees these too. A 403 means the
   * account is private, not that something failed.
   */
  forUser(username: string, query: LibraryQuery = {}): Observable<LibraryPageDto> {
    return this.http.get<LibraryPageDto>(
      `${environment.apiBase}/users/${encodeURIComponent(username)}/library`,
      { params: toParams(query) },
    );
  }

  filtersFor(username: string): Observable<LibraryFiltersDto> {
    return this.http.get<LibraryFiltersDto>(
      `${environment.apiBase}/users/${encodeURIComponent(username)}/library/filters`,
    );
  }
}

/**
 * Only fields that are actually set become parameters.
 *
 * Sending `genreId=` or `desc=null` is not the same as sending nothing: the server reads an absent
 * parameter as "no opinion" and applies its own default, which is exactly what an unset control
 * means here.
 */
function toParams(query: LibraryQuery): HttpParams {
  let params = new HttpParams();

  for (const [key, value] of Object.entries(query)) {
    if (value === null || value === undefined) continue;
    if (typeof value === 'string' && value.trim() === '') continue;
    params = params.set(key, String(value));
  }

  return params;
}
