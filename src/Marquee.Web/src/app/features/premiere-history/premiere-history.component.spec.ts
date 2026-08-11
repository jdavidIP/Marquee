import { TestBed, fakeAsync, tick } from '@angular/core/testing';
import { signal } from '@angular/core';
import { provideRouter } from '@angular/router';
import { HttpErrorResponse } from '@angular/common/http';
import { of, throwError } from 'rxjs';
import { PremiereHistoryComponent } from './premiere-history.component';
import { PremiereHistoryService } from '../../core/premiere-history.service';
import { AuthService } from '../../core/auth.service';
import { PagedResult, PremiereHistoryEntryDto, PremiereHistoryQuery } from '../../core/models';

/**
 * The read-only history behind a profile's "premieres attended" count (issue #38). Mirrors
 * LibraryComponent's own tests: one query per control change, a 403 reads as "private" rather than
 * a generic error, and an empty result is distinguished from an empty history.
 */
describe('PremiereHistoryComponent', () => {
  let forUserSpy: jasmine.Spy;

  function entry(title: string, premiereId = title): PremiereHistoryEntryDto {
    return {
      premiereId,
      movie: {
        tmdbId: 1,
        title,
        posterUrl: null,
        releaseYear: 2001,
        overview: null,
        voteAverage: 7.5,
        voteCount: 100,
      },
      openedAt: '2026-01-01T00:00:00Z',
      status: 'Opened',
      clapCount: 3,
      emblemTier: 4,
    };
  }

  function page(items: PremiereHistoryEntryDto[], total = items.length): PagedResult<PremiereHistoryEntryDto> {
    return { items, total, page: 1, pageSize: 20 };
  }

  /** Defaults to a viewer who is not "ana"; the self case gets its own tests below. */
  function make(
    result: PagedResult<PremiereHistoryEntryDto> | (() => ReturnType<typeof throwError>) = page([entry('Alien')]),
    viewerUsername: string | null = 'someone-else',
  ) {
    TestBed.resetTestingModule();
    forUserSpy = jasmine.createSpy('forUser').and.returnValue(
      typeof result === 'function' ? result() : of(result),
    );

    TestBed.configureTestingModule({
      imports: [PremiereHistoryComponent],
      providers: [
        provideRouter([]),
        { provide: PremiereHistoryService, useValue: { forUser: forUserSpy } },
        { provide: AuthService, useValue: { user: signal(viewerUsername ? { username: viewerUsername } : null) } },
      ],
    });

    const fixture = TestBed.createComponent(PremiereHistoryComponent);
    fixture.componentRef.setInput('username', 'ana');
    fixture.detectChanges();
    return fixture.componentInstance as unknown as Record<string, any>;
  }

  function lastQuery(): PremiereHistoryQuery {
    return forUserSpy.calls.mostRecent().args[1];
  }

  it('asks for the first page with no filters on load', () => {
    make();

    expect(forUserSpy).toHaveBeenCalledTimes(1);
    expect(forUserSpy.calls.mostRecent().args[0]).toBe('ana');
    expect(lastQuery().page).toBe(1);
    expect(lastQuery().sort).toBe('Opened');
  });

  it('sends one search request per pause, not one per keystroke', fakeAsync(() => {
    const c = make();
    forUserSpy.calls.reset();

    c['onSearchInput']('a');
    c['onSearchInput']('al');
    c['onSearchInput']('ali');
    tick(300);

    expect(forUserSpy).toHaveBeenCalledTimes(1);
    expect(lastQuery().search).toBe('ali');
  }));

  it('goes back to page one when the search changes', () => {
    const c = make(page([entry('Alien')], 100));

    c['next']();
    expect(c['page']()).toBe(2);

    c['clearSearch']();
    expect(c['page']()).toBe(1);
  });

  it('drops a direction chosen for one sort field when another is picked', () => {
    const c = make();

    c['onSortChange']('Title');
    c['toggleDirection']();
    expect(c['descending']()).toBe(true);

    c['onSortChange']('Rating');
    expect(c['desc']()).toBeNull();
    expect(c['descending']()).toBe(true);
  });

  it('defaults a title sort to ascending and every other field to descending', () => {
    const c = make();

    c['onSortChange']('Title');
    expect(c['descending']()).toBe(false);

    c['onSortChange']('Opened');
    expect(c['descending']()).toBe(true);
  });

  it('reports an empty result as filtered out rather than as an empty history', () => {
    const c = make(page([]));

    expect(c['filtered']()).toBe(false);

    c['onSearchInput']('nothing');

    expect(c['filtered']()).toBe(true);
  });

  it('does not offer a next page when everything already fits on one', () => {
    const c = make(page([entry('Alien')], 1));

    expect(c['totalPages']()).toBe(1);
    expect(c['canNext']()).toBe(false);
  });

  it('pages through a total larger than one page', () => {
    const c = make(page([entry('Alien')], 50));

    expect(c['totalPages']()).toBe(3);

    c['next']();

    expect(lastQuery().page).toBe(2);
  });

  it('treats a 403 as "this account is private", not as an error banner', () => {
    const c = make(() => throwError(() => new HttpErrorResponse({ status: 403 })));

    expect(c['forbidden']()).toBe(true);
    expect(c['error']()).toBeNull();
  });

  it('treats any other failure as a real error, not as forbidden', () => {
    const c = make(() => throwError(() => new HttpErrorResponse({ status: 500 })));

    expect(c['forbidden']()).toBe(false);
    expect(c['error']()).not.toBeNull();
  });

  it('reloads with the new username when the route param changes on the same instance', () => {
    TestBed.resetTestingModule();
    forUserSpy = jasmine.createSpy('forUser').and.returnValue(of(page([entry('Alien')])));

    TestBed.configureTestingModule({
      imports: [PremiereHistoryComponent],
      providers: [
        provideRouter([]),
        { provide: PremiereHistoryService, useValue: { forUser: forUserSpy } },
        { provide: AuthService, useValue: { user: signal({ username: 'someone-else' }) } },
      ],
    });

    const fixture = TestBed.createComponent(PremiereHistoryComponent);
    fixture.componentRef.setInput('username', 'ana');
    fixture.detectChanges();
    forUserSpy.calls.reset();

    fixture.componentRef.setInput('username', 'bob');
    fixture.detectChanges();

    expect(forUserSpy).toHaveBeenCalledTimes(1);
    expect(forUserSpy.calls.mostRecent().args[0]).toBe('bob');
  });

  it('recognises the viewer looking at their own premiere history', () => {
    const c = make(page([entry('Alien')]), 'ana');

    expect(c['isSelf']()).toBe(true);
  });

  it('does not treat a stranger or an anonymous viewer as self', () => {
    expect(make(page([entry('Alien')]), 'someone-else')['isSelf']()).toBe(false);
    expect(make(page([entry('Alien')]), null)['isSelf']()).toBe(false);
  });
});
