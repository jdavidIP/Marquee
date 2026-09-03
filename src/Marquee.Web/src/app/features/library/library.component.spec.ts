import { TestBed, fakeAsync, tick } from '@angular/core/testing';
import { provideRouter } from '@angular/router';
import { HttpErrorResponse } from '@angular/common/http';
import { of, throwError } from 'rxjs';
import { LibraryComponent } from './library.component';
import { LibraryService } from '../../core/library.service';
import {
  LibraryEntryDto,
  LibraryFiltersDto,
  LibraryQuery,
  MyLibraryPageDto,
  PagedResult,
} from '../../core/models';

/**
 * The screen's job is to turn the controls into one query and send it, and to tell an empty library
 * apart from an empty result. Both are things it decides on its own — the server cannot — so both
 * are worth pinning down here.
 */
describe('LibraryComponent', () => {
  let mineSpy: jasmine.Spy;

  function entry(title: string, movieId = title): LibraryEntryDto {
    return {
      movieId,
      movie: {
        tmdbId: 1,
        title,
        posterUrl: null,
        releaseYear: 2001,
        overview: null,
        voteAverage: 7.5,
        voteCount: 100,
      },
      premiereId: 'p1',
      acquiredAt: '2026-01-01T00:00:00Z',
      emblemTier: 3,
      emblems: [{ tier: 3, scopeId: 'global' }],
    };
  }

  function page(items: LibraryEntryDto[], total = items.length): PagedResult<LibraryEntryDto> {
    return { items, total, page: 1, pageSize: 24 };
  }

  function myPage(
    items: LibraryEntryDto[],
    total = items.length,
    platinumCount = 0,
    premieresAttended = total,
  ): MyLibraryPageDto {
    return { ...page(items, total), platinumCount, premieresAttended };
  }

  const filters: LibraryFiltersDto = {
    genres: [
      { tmdbId: 18, name: 'Drama' },
      { tmdbId: 35, name: 'Comedy' },
    ],
    minYear: 1999,
    maxYear: 2001,
  };

  function make(result = page([entry('Alien')]), available: LibraryFiltersDto = filters) {
    TestBed.resetTestingModule();
    mineSpy = jasmine.createSpy('mine').and.returnValue(of(result));

    TestBed.configureTestingModule({
      imports: [LibraryComponent],
      providers: [
        provideRouter([]),
        { provide: LibraryService, useValue: { mine: mineSpy, filters: () => of(available) } },
      ],
    });

    const fixture = TestBed.createComponent(LibraryComponent);
    fixture.detectChanges();
    return fixture.componentInstance as unknown as Record<string, any>;
  }

  /** The query argument of the most recent call, which is what every assertion below is about. */
  function lastQuery(): LibraryQuery {
    return mineSpy.calls.mostRecent().args[0];
  }

  it('asks for the first page with no filters on load', () => {
    make();

    expect(mineSpy).toHaveBeenCalledTimes(1);
    expect(lastQuery().page).toBe(1);
    expect(lastQuery().genreId).toBeNull();
    expect(lastQuery().sort).toBe('Acquired');
  });

  it('offers the genres the API reported rather than a built-in list', () => {
    const c = make();

    expect(c['genres']().map((g: { name: string }) => g.name)).toEqual(['Drama', 'Comedy']);
  });

  it('builds a year list spanning the range the API reported, newest first', () => {
    const c = make();

    expect(c['years']()).toEqual([2001, 2000, 1999]);
  });

  it('sends one search request per pause, not one per keystroke', fakeAsync(() => {
    const c = make();
    mineSpy.calls.reset();

    c['onSearchInput']('a');
    c['onSearchInput']('al');
    c['onSearchInput']('ali');
    tick(300);

    expect(mineSpy).toHaveBeenCalledTimes(1);
    expect(lastQuery().search).toBe('ali');
  }));

  it('combines every control into a single query', fakeAsync(() => {
    const c = make();

    c['onGenreChange']('18');
    c['onMinYearChange']('2000');
    c['onSearchInput']('space');
    tick(300);

    expect(lastQuery().genreId).toBe(18);
    expect(lastQuery().minYear).toBe(2000);
    expect(lastQuery().search).toBe('space');
  }));

  it('goes back to page one whenever the filters change', () => {
    const c = make(page([entry('Alien')], 100));

    c['next']();
    expect(c['page']()).toBe(2);

    c['onGenreChange']('18');
    expect(c['page']()).toBe(1);
    expect(lastQuery().page).toBe(1);
  });

  it('drops a direction chosen for one sort field when another is picked', () => {
    // Descending means something different for a title than for a rating, so carrying the choice
    // across would apply an answer to a question nobody asked.
    const c = make();

    c['onSortChange']('Title');
    c['toggleDirection']();
    expect(c['descending']()).toBe(true);

    c['onSortChange']('Rating');
    expect(c['desc']()).toBeNull();
    expect(c['descending']()).toBe(true, 'rating reads best-first by default');
  });

  it('defaults a title sort to ascending and every other field to descending', () => {
    const c = make();

    c['onSortChange']('Title');
    expect(c['descending']()).toBe(false);

    c['onSortChange']('ReleaseYear');
    expect(c['descending']()).toBe(true);
  });

  it('reports an empty result as filtered out rather than as an empty library', () => {
    const c = make(page([]));

    expect(c['filtered']()).toBe(false, 'nothing is narrowing the list yet');

    c['onGenreChange']('18');

    expect(c['filtered']()).toBe(true);
  });

  it('shows the marquee-sign empty state, not the no-results copy, for a genuinely empty library', () => {
    TestBed.resetTestingModule();
    mineSpy = jasmine.createSpy('mine').and.returnValue(of(myPage([], 0)));

    TestBed.configureTestingModule({
      imports: [LibraryComponent],
      providers: [
        provideRouter([]),
        { provide: LibraryService, useValue: { mine: mineSpy, filters: () => of(filters) } },
      ],
    });

    const fixture = TestBed.createComponent(LibraryComponent);
    fixture.detectChanges();
    const text = (fixture.nativeElement as HTMLElement).textContent ?? '';

    expect(text).toContain('Watch the marquee');
    expect(text).not.toContain('Nothing in your library matches that');
  });

  it('clears every filter at once and reloads', fakeAsync(() => {
    const c = make();

    c['onGenreChange']('18');
    c['onSearchInput']('space');
    tick(300);

    c['clearFilters']();

    expect(c['filtered']()).toBe(false);
    expect(lastQuery().search).toBe('');
    expect(lastQuery().genreId).toBeNull();
  }));

  it('reads the header stats from the response for your own library', () => {
    const c = make(myPage([entry('Alien')], 12, 2, 18));

    expect(c['platinumCount']()).toBe(2);
    expect(c['premieresAttended']()).toBe(18);
    expect(c['headline']()).toBe('12 films, 12 nights');
  });

  it('reads the header stats singular for exactly one film', () => {
    const c = make(myPage([entry('Alien')], 1, 0, 1));

    expect(c['headline']()).toBe('1 film, 1 night');
  });

  it('does not offer a next page when everything already fits on one', () => {
    const c = make(page([entry('Alien')], 1));

    expect(c['totalPages']()).toBe(1);
    expect(c['canNext']()).toBe(false);
    expect(c['canPrevious']()).toBe(false);
  });

  it('pages through a total larger than one page', () => {
    const c = make(page([entry('Alien')], 50));

    expect(c['totalPages']()).toBe(3);
    expect(c['canNext']()).toBe(true);

    c['next']();

    expect(lastQuery().page).toBe(2);
    expect(c['canPrevious']()).toBe(true);
  });

  it('still lists the library when the filter values cannot be loaded', () => {
    // The listing works unfiltered, so a failure to populate the dropdowns must not put an error
    // banner over a screen that is otherwise fine.
    TestBed.resetTestingModule();
    mineSpy = jasmine.createSpy('mine').and.returnValue(of(page([entry('Alien')])));

    TestBed.configureTestingModule({
      imports: [LibraryComponent],
      providers: [
        provideRouter([]),
        {
          provide: LibraryService,
          useValue: {
            mine: mineSpy,
            filters: () => ({ subscribe: ({ error }: { error: (e: unknown) => void }) => error(new Error('nope')) }),
          },
        },
      ],
    });

    const fixture = TestBed.createComponent(LibraryComponent);
    fixture.detectChanges();
    const c = fixture.componentInstance as unknown as Record<string, any>;

    expect(c['error']()).toBeNull();
    expect(c['entries']().length).toBe(1);
    expect(c['genres']()).toEqual([]);
  });

  describe('viewing someone else\'s library (issue #38)', () => {
    let forUserSpy: jasmine.Spy;

    function makeForUser(
      username: string,
      result: MyLibraryPageDto | (() => ReturnType<typeof throwError>) = myPage([entry('Alien')]),
    ) {
      TestBed.resetTestingModule();
      forUserSpy = jasmine.createSpy('forUser').and.returnValue(
        typeof result === 'function' ? result() : of(result),
      );

      TestBed.configureTestingModule({
        imports: [LibraryComponent],
        providers: [
          provideRouter([]),
          {
            provide: LibraryService,
            useValue: {
              mine: jasmine.createSpy('mine should not be called for a routed username'),
              filters: jasmine.createSpy('filters should not be called for a routed username'),
              forUser: forUserSpy,
              filtersFor: () => of(filters),
            },
          },
        ],
      });

      const fixture = TestBed.createComponent(LibraryComponent);
      fixture.componentRef.setInput('username', username);
      fixture.detectChanges();
      return fixture;
    }

    it("reads someone else's header stats too — they describe the account, not the viewer", () => {
      const fixture = makeForUser('ana', myPage([entry('Alien')], 1, 3, 7));
      const c = fixture.componentInstance as unknown as Record<string, any>;

      expect(c['platinumCount']()).toBe(3);
      expect(c['premieresAttended']()).toBe(7);
    });

    it('hides the header stats once a newly-routed username turns out to be forbidden', () => {
      // Regression guard: navigating from a visible profile to a forbidden one must not leave the
      // previous profile's stats on screen just because the signals were never reset to zero.
      const fixture = makeForUser('ana', myPage([entry('Alien')], 1, 3, 7));
      let text = (fixture.nativeElement as HTMLElement).textContent ?? '';
      expect(text).toContain('Platinum');

      forUserSpy.and.returnValue(throwError(() => new HttpErrorResponse({ status: 403 })));
      fixture.componentRef.setInput('username', 'bob');
      fixture.detectChanges();

      text = (fixture.nativeElement as HTMLElement).textContent ?? '';
      expect(text).not.toContain('Platinum');
    });

    it('calls forUser rather than mine when a username is routed', () => {
      makeForUser('ana');

      expect(forUserSpy).toHaveBeenCalledTimes(1);
      expect(forUserSpy.calls.mostRecent().args[0]).toBe('ana');
    });

    it('treats a 403 as "this account is private", not as an error banner', () => {
      const fixture = makeForUser('ana', () => throwError(() => new HttpErrorResponse({ status: 403 })));
      const c = fixture.componentInstance as unknown as Record<string, any>;

      expect(c['forbidden']()).toBe(true);
      expect(c['error']()).toBeNull();
    });

    it('treats any other failure as a real error, not as forbidden', () => {
      const fixture = makeForUser('ana', () => throwError(() => new HttpErrorResponse({ status: 500 })));
      const c = fixture.componentInstance as unknown as Record<string, any>;

      expect(c['forbidden']()).toBe(false);
      expect(c['error']()).not.toBeNull();
    });

    it('reloads with the new username when the route param changes on the same instance', () => {
      const fixture = makeForUser('ana');
      forUserSpy.calls.reset();

      fixture.componentRef.setInput('username', 'bob');
      fixture.detectChanges();

      expect(forUserSpy).toHaveBeenCalledTimes(1);
      expect(forUserSpy.calls.mostRecent().args[0]).toBe('bob');
    });
  });
});
