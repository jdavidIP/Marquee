import { Injectable } from '@angular/core';
import { HttpClient } from '@angular/common/http';
import { Observable } from 'rxjs';
import { environment } from '../../environments/environment';
import { AdminMetricsDto } from './models';

@Injectable({ providedIn: 'root' })
export class AdminService {
  constructor(private http: HttpClient) {}

  metrics(): Observable<AdminMetricsDto> {
    return this.http.get<AdminMetricsDto>(`${environment.apiBase}/admin/metrics`);
  }
}
