import { Component, computed, effect, inject, input, signal, untracked } from '@angular/core';
import { CommonModule } from '@angular/common';
import { Router } from '@angular/router';
import { AdminService } from '../../core/admin.service';
import { AuthService } from '../../core/auth.service';
import { apiError } from '../../core/http-error';
import { AdminUserDto } from '../../core/models';
import { ConfirmDialogComponent } from '../../shared/confirm-dialog.component';

const PAGE_SIZE = 25;
const SEARCH_DEBOUNCE_MS = 300;

@Component({
  selector: 'app-admin-users',
  standalone: true,
  imports: [CommonModule, ConfirmDialogComponent],
  templateUrl: './admin-users.component.html',
  styleUrls: ['./admin-tables.css', './admin-users.component.css'],
})
export class AdminUsersComponent {
  private readonly admin = inject(AdminService);
  private readonly auth = inject(AuthService);
  private readonly router = inject(Router);

  /** Bound from the query string, so a refresh or the back button lands on the same view. */
  readonly search = input<string>('');
  readonly page = input<string>('1');

  protected readonly users = signal<AdminUserDto[]>([]);
  protected readonly total = signal(0);
  protected readonly loading = signal(true);
  protected readonly refreshing = signal(false);
  protected readonly error = signal<string | null>(null);

  /** Only the row being acted on is disabled; a table of 25 must not freeze for one action. */
  protected readonly busyUserId = signal<string | null>(null);
  protected readonly pending = signal<AdminUserDto | null>(null);

  protected readonly canBlock = this.auth.canBlockUsers;
  protected readonly pageSize = PAGE_SIZE;

  protected readonly currentPage = computed(() => {
    const parsed = Number.parseInt(this.page(), 10);
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

  private searchTimer: ReturnType<typeof setTimeout> | null = null;

  constructor() {
    // One code path loads data — a query-param change — so typing, paging, refreshing and the back
    // button all arrive the same way.
    //
    // The load runs untracked, and that is not optional: it reads users() to tell a first load from
    // a refresh, and it writes users() with the result. Tracked, that read would make the effect
    // depend on its own output — every response would retrigger the effect, and the screen would
    // hammer the endpoint until the rate limiter stopped it.
    effect(() => {
      const search = this.search();
      const page = this.currentPage();
      untracked(() => this.load(search, page));
    });
  }

  /** Debounced so a search is one request per pause, not one per keystroke. */
  protected onSearchInput(value: string): void {
    if (this.searchTimer) clearTimeout(this.searchTimer);
    this.searchTimer = setTimeout(() => this.navigate({ search: value, page: 1 }), SEARCH_DEBOUNCE_MS);
  }

  protected clearSearch(): void {
    if (this.searchTimer) clearTimeout(this.searchTimer);
    this.navigate({ search: '', page: 1 });
  }

  protected goToPage(page: number): void {
    this.navigate({ page });
  }

  protected askToBlock(user: AdminUserDto): void {
    this.pending.set(user);
  }

  protected confirmBlock(reason: string | null): void {
    const user = this.pending();
    if (!user) return;

    this.busyUserId.set(user.id);
    this.admin.blockUser(user.id, reason).subscribe({
      next: () => {
        this.pending.set(null);
        this.busyUserId.set(null);
        this.patch(user.id, { isBlocked: true });
      },
      error: (err: unknown) => this.failAction(err, `Could not block ${user.username}.`),
    });
  }

  /** No confirmation: unblocking is reversible and restorative, so a dialog is pure friction. */
  protected unblock(user: AdminUserDto): void {
    this.busyUserId.set(user.id);
    this.admin.unblockUser(user.id).subscribe({
      next: () => {
        this.busyUserId.set(null);
        this.patch(user.id, { isBlocked: false });
      },
      error: (err: unknown) => this.failAction(err, `Could not unblock ${user.username}.`),
    });
  }

  /** Mirrors the API's own refusal (AdminController) rather than letting an admin discover it. */
  protected isSelf(user: AdminUserDto): boolean {
    return user.id === this.auth.user()?.id;
  }

  private failAction(err: unknown, fallback: string): void {
    this.busyUserId.set(null);
    this.pending.set(null);
    this.error.set(apiError(err, fallback));

    // The row disagreeing with the server is what caused this, so re-read rather than guess.
    this.load(this.search(), this.currentPage());
  }

  /**
   * Safe to patch rather than refetch: the list is ordered by username and filtered by name or
   * email, and blocking changes neither — so the row cannot end up in the wrong place or the wrong
   * result set.
   */
  private patch(id: string, changes: Partial<AdminUserDto>): void {
    this.users.update((rows) => rows.map((u) => (u.id === id ? { ...u, ...changes } : u)));
  }

  private navigate(queryParams: Record<string, string | number>): void {
    this.router.navigate([], {
      queryParams,
      queryParamsHandling: 'merge',
      // Keystrokes should not each become a history entry to back out through.
      replaceUrl: true,
    });
  }

  private load(search: string, page: number): void {
    // First load shows a message; later ones dim the table instead of blanking it.
    if (this.users().length === 0) this.loading.set(true);
    else this.refreshing.set(true);

    this.admin.users({ search, page, pageSize: PAGE_SIZE }).subscribe({
      next: (result) => {
        this.users.set(result.items);
        this.total.set(result.total);
        this.error.set(null);
        this.loading.set(false);
        this.refreshing.set(false);
      },
      error: (err: unknown) => {
        // Existing rows stay: losing them because one poll failed removes what someone came to read.
        this.error.set(apiError(err, 'Could not load users.'));
        this.loading.set(false);
        this.refreshing.set(false);
      },
    });
  }
}
