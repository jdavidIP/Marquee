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

  it('encodes a username into the profile path', () => {
    // Usernames are free-form enough that a raw interpolation would build a broken URL for
    // anything needing escaping.
    service.profile('a b').subscribe();

    const req = http.expectOne(`${base}/a%20b`);
    expect(req.request.method).toBe('GET');
    req.flush({ username: 'a b', bio: null });
  });

  it('patches only the fields it was given', () => {
    // Both are optional on the API so a client can change one without clobbering the other —
    // sending isPrivate here would overwrite a setting this call never intended to touch.
    service.updateMe({ bio: 'hello' }).subscribe();

    const req = http.expectOne(`${base}/me`);
    expect(req.request.method).toBe('PATCH');
    expect(req.request.body).toEqual({ bio: 'hello' });
    req.flush({});
  });
});
