import { Injectable } from '@angular/core';
import { HttpClient, HttpParams } from '@angular/common/http';
import { Observable } from 'rxjs';
import { environment } from '../../environments/environment';
import { LibraryEntryDto, LibraryFiltersDto, LibraryQuery, PagedResult } from './models';

@Injectable({ providedIn: 'root' })
export class LibraryService {
  constructor(private http: HttpClient) {}

  mine(query: LibraryQuery = {}): Observable<PagedResult<LibraryEntryDto>> {
    return this.http.get<PagedResult<LibraryEntryDto>>(`${environment.apiBase}/library`, {
      params: toParams(query),
    });
  }

  filters(): Observable<LibraryFiltersDto> {
    return this.http.get<LibraryFiltersDto>(`${environment.apiBase}/library/filters`);
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
