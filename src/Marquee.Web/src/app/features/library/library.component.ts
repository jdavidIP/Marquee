import { Component, OnDestroy, OnInit, computed, inject, signal } from '@angular/core';
import { DecimalPipe } from '@angular/common';
import { LibraryService } from '../../core/library.service';
import { apiError } from '../../core/http-error';
import { GenreDto, LibraryEntryDto, LibraryQuery, LibrarySort } from '../../core/models';

const SEARCH_DEBOUNCE_MS = 300;

/** Matches the API's own default, so the first page asked for is the page it would have sent. */
const PAGE_SIZE = 24;

interface SortOption {
  readonly value: LibrarySort;
  readonly label: string;
}

@Component({
  selector: 'app-library',
  standalone: true,
  imports: [DecimalPipe],
  templateUrl: './library.component.html',
  styleUrl: './library.component.css',
})
export class LibraryComponent implements OnInit, OnDestroy {
  private readonly library = inject(LibraryService);

  protected readonly entries = signal<LibraryEntryDto[]>([]);
  protected readonly total = signal(0);
  protected readonly page = signal(1);
  protected readonly loading = signal(true);
  protected readonly error = signal<string | null>(null);

  /** Available filter values, as the API reports them for this library. */
  protected readonly genres = signal<GenreDto[]>([]);
  protected readonly years = signal<number[]>([]);

  protected readonly search = signal('');
  protected readonly genreId = signal<number | null>(null);
  protected readonly minYear = signal<number | null>(null);
  protected readonly maxYear = signal<number | null>(null);
  protected readonly sort = signal<LibrarySort>('Acquired');
  protected readonly desc = signal<boolean | null>(null);

  protected readonly sortOptions: readonly SortOption[] = [
    { value: 'Acquired', label: 'Recently acquired' },
    { value: 'Title', label: 'Title' },
    { value: 'ReleaseYear', label: 'Release year' },
    { value: 'Rating', label: 'Rating' },
  ];

  /**
   * Whether anything is narrowing the list. Drives the "clear" affordance and, more importantly,
   * tells an empty result apart from an empty library — "no movies yet" and "nothing matched" call
   * for completely different words.
   */
  protected readonly filtered = computed(
    () =>
      this.search().trim() !== '' ||
      this.genreId() !== null ||
      this.minYear() !== null ||
      this.maxYear() !== null,
  );

  protected readonly totalPages = computed(() => Math.max(1, Math.ceil(this.total() / PAGE_SIZE)));
  protected readonly canPrevious = computed(() => this.page() > 1);
  protected readonly canNext = computed(() => this.page() < this.totalPages());

  /** Ascending is only worth offering once there is a field whose order means something. */
  protected readonly descending = computed(() => this.desc() ?? this.sort() !== 'Title');

  private searchTimer: ReturnType<typeof setTimeout> | null = null;

  ngOnInit(): void {
    this.loadFilters();
    this.load();
  }

  /** A pending debounce would otherwise fire a request against a component nobody is looking at. */
  ngOnDestroy(): void {
    if (this.searchTimer) clearTimeout(this.searchTimer);
  }

  protected onSearchInput(value: string): void {
    this.search.set(value);
    if (this.searchTimer) clearTimeout(this.searchTimer);

    // One request per pause, not one per keystroke.
    this.searchTimer = setTimeout(() => this.reset(), SEARCH_DEBOUNCE_MS);
  }

  protected onGenreChange(value: string): void {
    this.genreId.set(value === '' ? null : Number(value));
    this.reset();
  }

  protected onMinYearChange(value: string): void {
    this.minYear.set(value === '' ? null : Number(value));
    this.reset();
  }

  protected onMaxYearChange(value: string): void {
    this.maxYear.set(value === '' ? null : Number(value));
    this.reset();
  }

  protected onSortChange(value: string): void {
    this.sort.set(value as LibrarySort);
    // Direction goes back to whatever the new field normally reads as, rather than carrying over a
    // choice made about a different field — descending means something else for a title than for a
    // rating.
    this.desc.set(null);
    this.reset();
  }

  protected toggleDirection(): void {
    this.desc.set(!this.descending());
    this.reset();
  }

  protected clearFilters(): void {
    if (this.searchTimer) clearTimeout(this.searchTimer);
    this.search.set('');
    this.genreId.set(null);
    this.minYear.set(null);
    this.maxYear.set(null);
    this.reset();
  }

  protected previous(): void {
    if (this.canPrevious()) this.goTo(this.page() - 1);
  }

  protected next(): void {
    if (this.canNext()) this.goTo(this.page() + 1);
  }

  protected emblemLabel(tier: number | null): string {
    return tier ? `Emblem tier ${tier}` : 'No emblem';
  }

  /** Any change to what is being asked for starts again at page one. */
  private reset(): void {
    this.page.set(1);
    this.load();
  }

  private goTo(page: number): void {
    this.page.set(page);
    this.load();
  }

  private load(): void {
    this.loading.set(true);

    const query: LibraryQuery = {
      search: this.search().trim(),
      genreId: this.genreId(),
      minYear: this.minYear(),
      maxYear: this.maxYear(),
      sort: this.sort(),
      desc: this.desc(),
      page: this.page(),
      pageSize: PAGE_SIZE,
    };

    this.library.mine(query).subscribe({
      next: (result) => {
        this.entries.set(result.items);
        this.total.set(result.total);
        this.loading.set(false);
        this.error.set(null);
      },
      error: (err: unknown) => {
        this.loading.set(false);
        this.error.set(apiError(err, 'Could not load your library.'));
      },
    });
  }

  /**
   * Loaded once. The values describe the library as a whole, so narrowing the view must not narrow
   * the controls — a genre dropdown that lost its other options the moment one was picked would
   * leave no way back to them.
   */
  private loadFilters(): void {
    this.library.filters().subscribe({
      next: (filters) => {
        this.genres.set(filters.genres);
        this.years.set(yearRange(filters.minYear, filters.maxYear));
      },
      // Deliberately quiet: the listing itself still works unfiltered, and an error banner about
      // dropdown contents would sit on top of a screen that is otherwise fine.
      error: () => {},
    });
  }
}

/** Newest first, so the years people are most likely to reach for are at the top of the list. */
function yearRange(min: number | null, max: number | null): number[] {
  if (min === null || max === null || max < min) return [];
  return Array.from({ length: max - min + 1 }, (_, i) => max - i);
}
