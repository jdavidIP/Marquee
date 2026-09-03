import { Component, OnDestroy, computed, effect, inject, input, signal, untracked } from '@angular/core';
import { DecimalPipe } from '@angular/common';
import { RouterLink } from '@angular/router';
import { LibraryService } from '../../core/library.service';
import { UsersService } from '../../core/users.service';
import { FriendsService } from '../../core/friends.service';
import { apiError, isForbidden } from '../../core/http-error';
import { GenreDto, LibraryEntryDto, LibraryQuery, LibrarySort, ProfileDto } from '../../core/models';
import { EmblemTicketComponent } from '../../shared/emblem-ticket.component';
import { initialsOf } from '../../core/avatar';

const SEARCH_DEBOUNCE_MS = 300;

/** Matches the API's own default, so the first page asked for is the page it would have sent. */
const PAGE_SIZE = 24;

/** Purely decorative — the empty state's dimmed marquee sign, unlit and static. */
const DIM_BULB_COUNT = Array.from({ length: 16 });

interface SortOption {
  readonly value: LibrarySort;
  readonly label: string;
}

@Component({
  selector: 'app-library',
  standalone: true,
  imports: [DecimalPipe, RouterLink, EmblemTicketComponent],
  templateUrl: './library.component.html',
  styleUrl: './library.component.css',
})
export class LibraryComponent implements OnDestroy {
  private readonly library = inject(LibraryService);
  private readonly users = inject(UsersService);
  private readonly friends = inject(FriendsService);

  protected readonly dimBulbs = DIM_BULB_COUNT;
  protected readonly ghostTiles = Array.from({ length: 12 });

  /**
   * Set only when routed at /u/:username/library — viewing someone else's collection rather than
   * your own (issue #38). Optional because the plain /library route carries no username segment at
   * all, so this input is never bound there and "your own library" stays the default reading.
   */
  readonly username = input<string | undefined>();

  protected readonly entries = signal<LibraryEntryDto[]>([]);
  protected readonly total = signal(0);
  protected readonly page = signal(1);
  protected readonly loading = signal(true);
  protected readonly error = signal<string | null>(null);
  /** A 403 from a private account being viewed — reads as "private", not as a failure. */
  protected readonly forbidden = signal(false);

  /** Describe the account being viewed, not the viewer — shown for anyone entitled to the entries. */
  protected readonly platinumCount = signal(0);
  protected readonly premieresAttended = signal(0);

  /**
   * The target's profile — fetched only when viewing someone else's library. Profile always
   * resolves (privacy restricts detail, not existence), independently of whether the library
   * itself 403s, which is exactly what lets the private-library lock screen still show an avatar,
   * a relationship pill and the shared-Premieres teaser.
   */
  protected readonly profile = signal<ProfileDto | null>(null);
  protected readonly sendingRequest = signal(false);

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

  /**
   * "Twelve films, eighteen nights" — films is the current (possibly filtered) result count;
   * nights is premieresAttended, which can run ahead of it when a film was re-premiered and
   * attended more than once (one film, two nights).
   */
  protected readonly headline = computed(() => {
    const films = this.total();
    const nights = this.premieresAttended();
    return `${films} film${films === 1 ? '' : 's'}, ${nights} night${nights === 1 ? '' : 's'}`;
  });

  protected readonly avatarUrl = computed(() => this.profile()?.avatarUrl ?? null);
  protected readonly bio = computed(() => this.profile()?.bio ?? null);
  protected readonly sharedNights = computed(() => this.profile()?.sharedPremieresAttended ?? null);

  protected readonly initials = computed(() => {
    const name = this.username();
    return name ? initialsOf(name) : '';
  });

  /** Mirrors ProfileComponent's own relationship/canAdd logic — same fields, same meaning. */
  protected readonly relationship = computed(() => {
    const p = this.profile();
    if (!p) return null;
    if (p.friendshipStatus === 'Accepted') return 'Friends';
    if (p.friendshipStatus === 'Pending') {
      return p.friendRequestOutgoing ? 'Request sent' : 'Wants to be friends';
    }
    return null;
  });

  protected readonly canAddFriend = computed(
    () => this.profile() !== null && this.profile()!.friendshipStatus === null,
  );

  private searchTimer: ReturnType<typeof setTimeout> | null = null;

  constructor() {
    // Keyed on username() rather than a one-shot ngOnInit: navigating from one person's library to
    // another's reuses this component instance (same route, different param), and only an effect
    // observes that. Untracked because loadFilters()/load() write the signals this would otherwise
    // depend on.
    effect(() => {
      this.username();
      untracked(() => {
        this.loadFilters();
        this.loadProfile();
        this.reset();
      });
    });
  }

  /**
   * Sends the request, then reloads rather than patching the signal optimistically — same
   * approach as ProfileComponent's addFriend, so the pill reflects whatever the server actually
   * recorded (an already-pending request from the other side resolves to "Friends" server-side,
   * not to what a naive client-side guess would show).
   */
  protected addFriend(): void {
    const name = this.username();
    if (!name) return;

    this.sendingRequest.set(true);
    this.friends.sendRequest(name).subscribe({
      next: () => {
        this.sendingRequest.set(false);
        this.loadProfile();
      },
      error: () => this.sendingRequest.set(false),
    });
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

  /** "3 days ago", "2 weeks ago", "1 month ago" — coarse on purpose, this is flavor text on a card. */
  protected acquiredLabel(acquiredAt: string): string {
    const days = Math.floor((Date.now() - new Date(acquiredAt).getTime()) / 86_400_000);
    if (days <= 0) return 'Today';
    if (days === 1) return '1 day ago';
    if (days < 7) return `${days} days ago`;
    const weeks = Math.floor(days / 7);
    if (weeks < 5) return weeks === 1 ? '1 week ago' : `${weeks} weeks ago`;
    const months = Math.floor(days / 30);
    if (months < 12) return months <= 1 ? '1 month ago' : `${months} months ago`;
    const years = Math.floor(days / 365);
    return years <= 1 ? '1 year ago' : `${years} years ago`;
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

    const username = this.username();
    const call = username ? this.library.forUser(username, query) : this.library.mine(query);

    call.subscribe({
      next: (result) => {
        this.entries.set(result.items);
        this.total.set(result.total);
        // Describes the account being viewed, not the viewer — shown for anyone entitled to see
        // the entries at all (self, a friend, or a public account), same as the entries themselves.
        this.platinumCount.set(result.platinumCount);
        this.premieresAttended.set(result.premieresAttended);
        this.loading.set(false);
        this.forbidden.set(false);
        this.error.set(null);
      },
      error: (err: unknown) => {
        this.loading.set(false);
        this.entries.set([]);
        // 403 reads as "this account is private", never as a generic error banner — it is an
        // expected outcome of the entitlement rule the API applies, not a failure.
        if (isForbidden(err)) {
          this.forbidden.set(true);
          this.error.set(null);
        } else {
          this.forbidden.set(false);
          this.error.set(apiError(err, username ? `Could not load ${username}'s library.` : 'Could not load your library.'));
        }
      },
    });
  }

  /**
   * Loaded once per username. The values describe the library as a whole, so narrowing the view
   * must not narrow the controls — a genre dropdown that lost its other options the moment one was
   * picked would leave no way back to them.
   */
  private loadFilters(): void {
    const username = this.username();
    const call = username ? this.library.filtersFor(username) : this.library.filters();

    call.subscribe({
      next: (filters) => {
        this.genres.set(filters.genres);
        this.years.set(yearRange(filters.minYear, filters.maxYear));
      },
      // Deliberately quiet: a private account's filters call fails the same way the listing does,
      // and that failure is already surfaced by load()'s own error handling — a second banner here
      // would just repeat it. For everyone else this stays what it always was: the listing still
      // works unfiltered, so nothing here should interrupt a screen that is otherwise fine.
      error: () => {},
    });
  }

  /**
   * Independent of load()'s own request: profile always resolves with 200 regardless of whether
   * the library itself 403s (privacy restricts detail, not existence), which is exactly what lets
   * the private-library lock screen still show an avatar, a relationship pill and the
   * shared-Premieres teaser.
   */
  private loadProfile(): void {
    const username = this.username();
    if (!username) {
      this.profile.set(null);
      return;
    }
    this.users.profile(username).subscribe({
      next: (p) => this.profile.set(p),
      error: () => this.profile.set(null),
    });
  }
}

/** Newest first, so the years people are most likely to reach for are at the top of the list. */
function yearRange(min: number | null, max: number | null): number[] {
  if (min === null || max === null || max < min) return [];
  return Array.from({ length: max - min + 1 }, (_, i) => max - i);
}
