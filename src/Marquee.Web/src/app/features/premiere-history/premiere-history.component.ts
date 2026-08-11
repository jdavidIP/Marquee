import {
  Component,
  OnDestroy,
  computed,
  effect,
  inject,
  input,
  signal,
  untracked,
} from '@angular/core';
import { DecimalPipe, DatePipe } from '@angular/common';
import { RouterLink } from '@angular/router';
import { PremiereHistoryService } from '../../core/premiere-history.service';
import { apiError, isForbidden } from '../../core/http-error';
import { PremiereHistoryEntryDto, PremiereHistorySort } from '../../core/models';
import { AuthService } from '../../core/auth.service';

const SEARCH_DEBOUNCE_MS = 300;
const PAGE_SIZE = 20;

interface SortOption {
  readonly value: PremiereHistorySort;
  readonly label: string;
}

/**
 * Which Premieres a user contributed to and what they earned each time (issue #38) — the history
 * behind the "premieres attended" count on a profile, which had no way to be opened before this.
 *
 * Reached at /u/:username/premieres for anyone, including the signed-in user's own — there is no
 * separate "my history" route, since the API entitles self the same way it entitles a friend.
 */
@Component({
  selector: 'app-premiere-history',
  standalone: true,
  imports: [DecimalPipe, DatePipe, RouterLink],
  templateUrl: './premiere-history.component.html',
  styleUrl: './premiere-history.component.css',
})
export class PremiereHistoryComponent implements OnDestroy {
  private readonly history = inject(PremiereHistoryService);
  private readonly auth = inject(AuthService);

  readonly username = input.required<string>();

  protected readonly entries = signal<PremiereHistoryEntryDto[]>([]);
  protected readonly total = signal(0);
  protected readonly page = signal(1);
  protected readonly loading = signal(true);
  protected readonly error = signal<string | null>(null);
  protected readonly forbidden = signal(false);

  protected readonly search = signal('');
  protected readonly sort = signal<PremiereHistorySort>('Opened');
  protected readonly desc = signal<boolean | null>(null);

  protected readonly sortOptions: readonly SortOption[] = [
    { value: 'Opened', label: 'Most recent' },
    { value: 'Title', label: 'Title' },
    { value: 'Rating', label: 'Rating' },
  ];

  protected readonly filtered = computed(() => this.search().trim() !== '');
  protected readonly totalPages = computed(() => Math.max(1, Math.ceil(this.total() / PAGE_SIZE)));
  protected readonly canPrevious = computed(() => this.page() > 1);
  protected readonly canNext = computed(() => this.page() < this.totalPages());
  /** Every field here reads best/most-recent first by default; Title is the one exception. */
  protected readonly descending = computed(() => this.desc() ?? this.sort() !== 'Title');

  private searchTimer: ReturnType<typeof setTimeout> | null = null;

  protected readonly isSelf = computed(() => {
    const f = this.username();
    return f !== null && f === this.auth.user()?.username;
  });

  constructor() {
    effect(() => {
      this.username();
      untracked(() => this.reset());
    });
  }

  ngOnDestroy(): void {
    if (this.searchTimer) clearTimeout(this.searchTimer);
  }

  protected onSearchInput(value: string): void {
    this.search.set(value);
    if (this.searchTimer) clearTimeout(this.searchTimer);
    this.searchTimer = setTimeout(() => this.reset(), SEARCH_DEBOUNCE_MS);
  }

  protected clearSearch(): void {
    if (this.searchTimer) clearTimeout(this.searchTimer);
    this.search.set('');
    this.reset();
  }

  protected onSortChange(value: string): void {
    this.sort.set(value as PremiereHistorySort);
    this.desc.set(null);
    this.reset();
  }

  protected toggleDirection(): void {
    this.desc.set(!this.descending());
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

    this.history
      .forUser(this.username(), {
        search: this.search().trim(),
        sort: this.sort(),
        desc: this.desc(),
        page: this.page(),
        pageSize: PAGE_SIZE,
      })
      .subscribe({
        next: (result) => {
          this.entries.set(result.items);
          this.total.set(result.total);
          this.loading.set(false);
          this.forbidden.set(false);
          this.error.set(null);
        },
        error: (err: unknown) => {
          this.loading.set(false);
          this.entries.set([]);
          if (isForbidden(err)) {
            this.forbidden.set(true);
            this.error.set(null);
          } else {
            this.forbidden.set(false);
            this.error.set(apiError(err, `Could not load ${this.username()}'s history.`));
          }
        },
      });
  }
}
