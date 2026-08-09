import { Component, computed, effect, inject, input, signal, untracked } from '@angular/core';
import { CommonModule } from '@angular/common';
import { Router } from '@angular/router';
import { AdminService } from '../../core/admin.service';
import { apiError } from '../../core/http-error';
import { AdminPremiereDto, PremiereStatus } from '../../core/models';

const PAGE_SIZE = 25;

/** The filter options, in lifecycle order. Sentence case per CLAUDE.md §7. */
const STATUS_FILTERS: ReadonlyArray<{ value: PremiereStatus | ''; label: string }> = [
  { value: '', label: 'All' },
  { value: 'Scheduled', label: 'Scheduled' },
  { value: 'Active', label: 'Active' },
  { value: 'Opened', label: 'Opened' },
  { value: 'AutoOpened', label: 'Auto-opened' },
];

@Component({
  selector: 'app-admin-premieres',
  standalone: true,
  imports: [CommonModule],
  templateUrl: './admin-premieres.component.html',
  styleUrls: ['./admin-tables.css', './admin-premieres.component.css'],
})
export class AdminPremieresComponent {
  private readonly admin = inject(AdminService);
  private readonly router = inject(Router);

  /** Bound from the query string, so a refresh or the back button lands on the same view. */
  readonly status = input<string>('');
  readonly page = input<string>('1');

  protected readonly premieres = signal<AdminPremiereDto[]>([]);
  protected readonly total = signal(0);
  protected readonly loading = signal(true);
  protected readonly refreshing = signal(false);
  protected readonly error = signal<string | null>(null);

  protected readonly filters = STATUS_FILTERS;
  protected readonly pageSize = PAGE_SIZE;

  /**
   * withComponentInputBinding can hand an input undefined when the URL omits the param entirely,
   * overriding the declared default rather than falling back to it — so these are guarded rather
   * than trusted.
   */
  protected readonly activeFilter = computed<PremiereStatus | ''>(
    () => (this.status() ?? '') as PremiereStatus | '',
  );

  protected readonly currentPage = computed(() => {
    const parsed = Number.parseInt(this.page() ?? '1', 10);
    return Number.isFinite(parsed) && parsed > 0 ? parsed : 1;
  });

  protected readonly firstShown = computed(() =>
    this.total() === 0 ? 0 : (this.currentPage() - 1) * PAGE_SIZE + 1,
  );
  protected readonly lastShown = computed(() =>
    Math.min(this.currentPage() * PAGE_SIZE, this.total()),
  );
  protected readonly hasPrevious = computed(() => this.currentPage() > 1);
  protected readonly hasNext = computed(() => this.currentPage() * PAGE_SIZE < this.total());

  constructor() {
    // Same shape as the users screen: one code path loads data, driven by the query params, so
    // filtering, paging, refreshing and the back button all arrive the same way.
    //
    // untracked is load-bearing — load() reads premieres() to tell a first load from a refresh and
    // writes it with the result, so a tracked read would make this effect depend on its own output
    // and retrigger on every response.
    effect(() => {
      const status = this.activeFilter();
      const page = this.currentPage();
      untracked(() => this.load(status, page));
    });
  }

  protected selectFilter(status: PremiereStatus | ''): void {
    // null drops the parameter from the URL rather than leaving a bare `status=`, so a shared or
    // bookmarked "All" link reads as the unfiltered view it is.
    this.navigate({ status: status || null, page: 1 });
  }

  protected goToPage(page: number): void {
    this.navigate({ page });
  }

  protected refresh(): void {
    this.load(this.activeFilter(), this.currentPage());
  }

  // ------------------------------------------------------------------ presentation

  /** Pill tone per status. Active is the one that is live right now, so it gets the accent. */
  protected pillClass(status: PremiereStatus): string {
    switch (status) {
      case 'Active':
        return 'pill--warn';
      case 'Opened':
        return 'pill--ok';
      case 'AutoOpened':
        return 'pill--ok';
      default:
        return 'pill--idle';
    }
  }

  protected statusLabel(status: PremiereStatus): string {
    return status === 'AutoOpened' ? 'Auto-opened' : status;
  }

  protected isPending(p: AdminPremiereDto): boolean {
    return p.status === 'Scheduled';
  }

  /**
   * Whether the durable clap total is meaningful yet. TotalClaps is only written when a Premiere
   * opens — live counting happens in Redis — so showing a flat 0 next to a running Premiere would
   * read as "nobody clapped" rather than "not counted here yet".
   */
  protected hasDurableCount(p: AdminPremiereDto): boolean {
    return p.status === 'Opened' || p.status === 'AutoOpened';
  }

  protected progressPct(p: AdminPremiereDto): number {
    if (p.threshold <= 0) return 0;
    return Math.min(100, Math.round((p.totalClaps / p.threshold) * 100));
  }

  private navigate(queryParams: Record<string, string | number | null>): void {
    this.router.navigate([], { queryParams, queryParamsHandling: 'merge', replaceUrl: true });
  }

  private load(status: PremiereStatus | '', page: number): void {
    if (this.premieres().length === 0) this.loading.set(true);
    else this.refreshing.set(true);

    // An empty filter must become null, not an empty string: the API binds status to a nullable
    // enum and `status=` fails that binding with a 400.
    this.admin.premieres({ status: status || null, page, pageSize: PAGE_SIZE }).subscribe({
      next: (result) => {
        this.premieres.set(result.items);
        this.total.set(result.total);
        this.error.set(null);
        this.loading.set(false);
        this.refreshing.set(false);
      },
      error: (err: unknown) => {
        // Existing cards stay on screen; one failed load should not empty the view.
        this.error.set(apiError(err, 'Could not load Premieres.'));
        this.loading.set(false);
        this.refreshing.set(false);
      },
    });
  }
}
