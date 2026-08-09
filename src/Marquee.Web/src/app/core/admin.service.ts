import { Injectable } from '@angular/core';
import { HttpClient, HttpParams } from '@angular/common/http';
import { Observable } from 'rxjs';
import { environment } from '../../environments/environment';
import {
  AdminMetricsDto,
  AdminPremiereDto,
  AdminUserDto,
  CountryDto,
  GenreDto,
  MovieFilterRequest,
  MovieSearchResultDto,
  PagedResult,
  PremiereEditOptionsDto,
  PremiereStatus,
} from './models';

/**
 * The admin surface, kept a thin transport layer: no error shaping here — that lives in
 * http-error.ts so every screen reports failures the same way.
 */
@Injectable({ providedIn: 'root' })
export class AdminService {
  private readonly base = `${environment.apiBase}/admin`;

  constructor(private http: HttpClient) {}

  metrics(): Observable<AdminMetricsDto> {
    return this.http.get<AdminMetricsDto>(`${this.base}/metrics`);
  }

  // ---------------------------------------------------------------------- users

  users(query: { search?: string | null; page: number; pageSize: number }): Observable<PagedResult<AdminUserDto>> {
    let params = new HttpParams()
      .set('page', query.page)
      .set('pageSize', query.pageSize);

    const search = query.search?.trim();
    if (search) params = params.set('search', search);

    return this.http.get<PagedResult<AdminUserDto>>(`${this.base}/users`, { params });
  }

  blockUser(id: string, reason?: string | null): Observable<void> {
    return this.http.post<void>(`${this.base}/users/${id}/block`, { reason: reason?.trim() || null });
  }

  unblockUser(id: string): Observable<void> {
    return this.http.post<void>(`${this.base}/users/${id}/unblock`, {});
  }

  // ------------------------------------------------------------------ premieres

  premieres(query: {
    status?: PremiereStatus | null;
    page: number;
    pageSize: number;
  }): Observable<PagedResult<AdminPremiereDto>> {
    let params = new HttpParams().set('page', query.page).set('pageSize', query.pageSize);

    // Load-bearing: an empty `status=` fails the API's nullable-enum binding with a 400, so "all"
    // has to omit the parameter rather than send a blank one. Do not "simplify" this.
    if (query.status) params = params.set('status', query.status);

    return this.http.get<PagedResult<AdminPremiereDto>>(`${this.base}/premieres`, { params });
  }

  editOptions(id: string): Observable<PremiereEditOptionsDto> {
    return this.http.get<PremiereEditOptionsDto>(`${this.base}/premieres/${id}/edit-options`);
  }

  reschedule(id: string, scheduledForUtc: string): Observable<AdminPremiereDto> {
    return this.http.patch<AdminPremiereDto>(`${this.base}/premieres/${id}/schedule`, { scheduledForUtc });
  }

  setThreshold(id: string, threshold: number): Observable<AdminPremiereDto> {
    return this.http.patch<AdminPremiereDto>(`${this.base}/premieres/${id}/threshold`, { threshold });
  }

  activate(id: string): Observable<AdminPremiereDto> {
    return this.http.post<AdminPremiereDto>(`${this.base}/premieres/${id}/activate`, {});
  }

  // ---------------------------------------------------------------------- movie

  /** Re-roll the hidden film. An empty filter means the plain §4.6 pool. */
  regenerateMovie(id: string, filter?: MovieFilterRequest): Observable<AdminPremiereDto> {
    return this.http.post<AdminPremiereDto>(`${this.base}/premieres/${id}/movie`, filter ?? {});
  }

  /**
   * Set a specific film. A film still inside its cooldown is refused with a 409 unless
   * `acknowledgeCooldown` is set, so the override is always a deliberate second action.
   */
  setMovie(id: string, tmdbId: number, acknowledgeCooldown = false): Observable<AdminPremiereDto> {
    return this.http.put<AdminPremiereDto>(`${this.base}/premieres/${id}/movie`, {
      tmdbId,
      acknowledgeCooldown,
    });
  }

  searchMovies(query: string): Observable<MovieSearchResultDto[]> {
    const params = new HttpParams().set('query', query.trim());
    return this.http.get<MovieSearchResultDto[]>(`${this.base}/movies/search`, { params });
  }

  genres(): Observable<GenreDto[]> {
    return this.http.get<GenreDto[]>(`${this.base}/movies/genres`);
  }

  countries(): Observable<CountryDto[]> {
    return this.http.get<CountryDto[]>(`${this.base}/movies/countries`);
  }
}
