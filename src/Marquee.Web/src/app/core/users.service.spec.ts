import { TestBed } from '@angular/core/testing';
import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { UsersService } from './users.service';
import { environment } from '../../environments/environment';

describe('UsersService', () => {
  let service: UsersService;
  let http: HttpTestingController;
  const base = `${environment.apiBase}/users`;

  beforeEach(() => {
    TestBed.configureTestingModule({
      providers: [UsersService, provideHttpClient(), provideHttpClientTesting()],
    });
    service = TestBed.inject(UsersService);
    http = TestBed.inject(HttpTestingController);
  });

  afterEach(() => http.verify());

  it('sends the query and a default limit', () => {
    service.search('an').subscribe();

    const req = http.expectOne((r) => r.url === base);
    expect(req.request.params.get('query')).toBe('an');
    expect(req.request.params.get('limit')).toBe('10');
    req.flush([]);
  });

  it('sends an explicit limit when given one', () => {
    service.search('an', 25).subscribe();

    const req = http.expectOne((r) => r.url === base);
    expect(req.request.params.get('limit')).toBe('25');
    req.flush([]);
  });
});
