import { TestBed } from '@angular/core/testing';
import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { AdminService } from './admin.service';
import { environment } from '../../environments/environment';

describe('AdminService', () => {
  let service: AdminService;
  let http: HttpTestingController;
  const base = `${environment.apiBase}/admin`;

  beforeEach(() => {
    TestBed.configureTestingModule({
      providers: [AdminService, provideHttpClient(), provideHttpClientTesting()],
    });
    service = TestBed.inject(AdminService);
    http = TestBed.inject(HttpTestingController);
  });

  afterEach(() => http.verify());

  describe('premieres', () => {
    it('omits status entirely when no filter is chosen', () => {
      // Load-bearing: the API binds status to a nullable enum, and an empty `status=` is a 400 —
      // so "all" must leave the parameter off rather than send a blank one.
      service.premieres({ status: null, page: 1, pageSize: 25 }).subscribe();

      const req = http.expectOne((r) => r.url === `${base}/premieres`);
      expect(req.request.params.has('status')).toBe(false);
      expect(req.request.params.get('page')).toBe('1');
      req.flush({ items: [], total: 0, page: 1, pageSize: 25 });
    });

    it('sends the status name when one is chosen', () => {
      service.premieres({ status: 'Scheduled', page: 2, pageSize: 10 }).subscribe();

      const req = http.expectOne((r) => r.url === `${base}/premieres`);
      expect(req.request.params.get('status')).toBe('Scheduled');
      expect(req.request.params.get('page')).toBe('2');
      req.flush({ items: [], total: 0, page: 2, pageSize: 10 });
    });
  });

  describe('users', () => {
    it('omits a blank search rather than sending an empty parameter', () => {
      service.users({ search: '   ', page: 1, pageSize: 25 }).subscribe();

      const req = http.expectOne((r) => r.url === `${base}/users`);
      expect(req.request.params.has('search')).toBe(false);
      req.flush({ items: [], total: 0, page: 1, pageSize: 25 });
    });

    it('trims a search term', () => {
      service.users({ search: '  ana  ', page: 1, pageSize: 25 }).subscribe();

      const req = http.expectOne((r) => r.url === `${base}/users`);
      expect(req.request.params.get('search')).toBe('ana');
      req.flush({ items: [], total: 0, page: 1, pageSize: 25 });
    });

    it('sends a null reason when none was given, so the body shape stays constant', () => {
      service.blockUser('u1').subscribe();

      const req = http.expectOne(`${base}/users/u1/block`);
      expect(req.request.body).toEqual({ reason: null });
      req.flush(null);
    });
  });

  describe('premiere edits', () => {
    it('PATCHes the schedule', () => {
      service.reschedule('p1', '2026-08-09T13:00:00.000Z').subscribe();

      const req = http.expectOne(`${base}/premieres/p1/schedule`);
      expect(req.request.method).toBe('PATCH');
      expect(req.request.body).toEqual({ scheduledForUtc: '2026-08-09T13:00:00.000Z' });
      req.flush({});
    });

    it('PATCHes the threshold', () => {
      service.setThreshold('p1', 120).subscribe();

      const req = http.expectOne(`${base}/premieres/p1/threshold`);
      expect(req.request.method).toBe('PATCH');
      expect(req.request.body).toEqual({ threshold: 120 });
      req.flush({});
    });
  });

  describe('movie selection', () => {
    it('does not acknowledge the cooldown unless asked', () => {
      // The override has to be a deliberate second action, never a default.
      service.setMovie('p1', 603).subscribe();

      const req = http.expectOne(`${base}/premieres/p1/movie`);
      expect(req.request.method).toBe('PUT');
      expect(req.request.body).toEqual({ tmdbId: 603, acknowledgeCooldown: false });
      req.flush({});
    });

    it('acknowledges the cooldown when told to', () => {
      service.setMovie('p1', 603, true).subscribe();

      const req = http.expectOne(`${base}/premieres/p1/movie`);
      expect(req.request.body).toEqual({ tmdbId: 603, acknowledgeCooldown: true });
      req.flush({});
    });

    it('POSTs an empty filter when re-rolling unconstrained', () => {
      service.regenerateMovie('p1').subscribe();

      const req = http.expectOne(`${base}/premieres/p1/movie`);
      expect(req.request.method).toBe('POST');
      expect(req.request.body).toEqual({});
      req.flush({});
    });

    it('passes a filter through when one is given', () => {
      service.regenerateMovie('p1', { genreId: 18, minYear: 1990 }).subscribe();

      const req = http.expectOne(`${base}/premieres/p1/movie`);
      expect(req.request.body).toEqual({ genreId: 18, minYear: 1990 });
      req.flush({});
    });
  });
});
