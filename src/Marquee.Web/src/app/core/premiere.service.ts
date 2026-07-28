import { Injectable } from '@angular/core';
import { HttpClient } from '@angular/common/http';
import { Observable } from 'rxjs';
import { environment } from '../../environments/environment';
import { ClapResponse, PremiereDto } from './models';

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

  clap(id: string): Observable<ClapResponse> {
    return this.http.post<ClapResponse>(`${environment.apiBase}/premieres/${id}/clap`, {});
  }

  /** Admin-only: manually create (and immediately activate) a Premiere. */
  create(): Observable<PremiereDto> {
    return this.http.post<PremiereDto>(`${environment.apiBase}/premieres`, {});
  }
}
