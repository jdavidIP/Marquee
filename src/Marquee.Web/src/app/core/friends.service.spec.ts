import { TestBed } from '@angular/core/testing';
import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { FriendsService } from './friends.service';
import { environment } from '../../environments/environment';

describe('FriendsService', () => {
  let service: FriendsService;
  let http: HttpTestingController;
  const base = `${environment.apiBase}/friends`;

  beforeEach(() => {
    TestBed.configureTestingModule({
      providers: [FriendsService, provideHttpClient(), provideHttpClientTesting()],
    });
    service = TestBed.inject(FriendsService);
    http = TestBed.inject(HttpTestingController);
  });

  afterEach(() => http.verify());

  it('reads friends from the collection root', () => {
    service.friends().subscribe();

    const req = http.expectOne(base);
    expect(req.request.method).toBe('GET');
    req.flush([]);
  });

  it('reads pending requests in both directions from one endpoint', () => {
    // Incoming and outgoing come back together, each flagged with `outgoing` — there is no
    // per-direction endpoint to call twice.
    service.requests().subscribe();

    const req = http.expectOne(`${base}/requests`);
    expect(req.request.method).toBe('GET');
    req.flush([]);
  });

  it('sends a request by username, not by id', () => {
    // The endpoint takes a username: the whole point is asking for someone you found by name.
    service.sendRequest('ana').subscribe();

    const req = http.expectOne(`${base}/requests`);
    expect(req.request.method).toBe('POST');
    expect(req.request.body).toEqual({ username: 'ana' });
    req.flush({});
  });

  it('accepts and rejects by request id', () => {
    service.accept('req-1').subscribe();
    const accept = http.expectOne(`${base}/requests/req-1/accept`);
    expect(accept.request.method).toBe('POST');
    accept.flush({});

    service.reject('req-2').subscribe();
    const reject = http.expectOne(`${base}/requests/req-2/reject`);
    expect(reject.request.method).toBe('POST');
    reject.flush({});
  });

  it('removes a friendship by the other user id, not a friendship id', () => {
    // Easy to get wrong, and it would 404 rather than fail loudly in review: the route is
    // /api/friends/{userId}, and FriendDto carries userId for exactly this reason.
    service.remove('user-9').subscribe();

    const req = http.expectOne(`${base}/user-9`);
    expect(req.request.method).toBe('DELETE');
    req.flush(null);
  });
});
