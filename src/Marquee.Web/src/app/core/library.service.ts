import { Injectable } from '@angular/core';
import { HttpClient } from '@angular/common/http';
import { Observable } from 'rxjs';
import { environment } from '../../environments/environment';
import { LibraryEntryDto } from './models';

@Injectable({ providedIn: 'root' })
export class LibraryService {
  constructor(private http: HttpClient) {}

  mine(): Observable<LibraryEntryDto[]> {
    return this.http.get<LibraryEntryDto[]>(`${environment.apiBase}/library`);
  }
}
