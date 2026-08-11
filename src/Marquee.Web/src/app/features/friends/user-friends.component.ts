import { Component, OnDestroy, effect, inject, input, signal, untracked } from '@angular/core';
import { RouterLink } from '@angular/router';
import { UsersService } from '../../core/users.service';
import { apiError, isForbidden } from '../../core/http-error';
import { FriendDto } from '../../core/models';

const SEARCH_DEBOUNCE_MS = 300;

/**
 * Someone else's friend list (issue #39) — a read-only counterpart to FriendsComponent, which is
 * the signed-in user's own. No accept/reject/remove here: those actions only make sense on your own
 * relationships, and the API this reads from (GET /api/users/{username}/friends) has no such calls.
 */
@Component({
  selector: 'app-user-friends',
  standalone: true,
  imports: [RouterLink],
  templateUrl: './user-friends.component.html',
  styleUrl: './user-friends.component.css',
})
export class UserFriendsComponent implements OnDestroy {
  private readonly users = inject(UsersService);

  /** Bound from the route, so /u/:username/friends is a real, linkable, reloadable address. */
  readonly username = input.required<string>();

  protected readonly friends = signal<FriendDto[]>([]);
  protected readonly loading = signal(true);
  protected readonly error = signal<string | null>(null);
  protected readonly forbidden = signal(false);

  protected readonly query = signal('');
  private searchTimer: ReturnType<typeof setTimeout> | null = null;

  constructor() {
    // Same one-path-loads-it shape as ProfileComponent: a route change, a reload, and a search all
    // arrive through this. Untracked because load() writes the signals this effect would otherwise
    // depend on.
    effect(() => {
      const name = this.username();
      untracked(() => this.load(name, ''));
    });
  }

  ngOnDestroy(): void {
    if (this.searchTimer) clearTimeout(this.searchTimer);
  }

  protected onSearchInput(value: string): void {
    this.query.set(value);
    if (this.searchTimer) clearTimeout(this.searchTimer);

    // One request per pause, not one per keystroke.
    this.searchTimer = setTimeout(() => this.load(this.username(), value.trim()), SEARCH_DEBOUNCE_MS);
  }

  protected clearSearch(): void {
    if (this.searchTimer) clearTimeout(this.searchTimer);
    this.query.set('');
    this.load(this.username(), '');
  }

  private load(username: string, search: string): void {
    this.loading.set(true);
    this.users.friendsOf(username, search).subscribe({
      next: (friends) => {
        this.friends.set(friends);
        this.loading.set(false);
        this.forbidden.set(false);
        this.error.set(null);
      },
      error: (err: unknown) => {
        this.loading.set(false);
        this.friends.set([]);
        // 403 reads as "this account is private", never as a generic error banner — it is an
        // expected outcome of the entitlement rule, not a failure.
        if (isForbidden(err)) {
          this.forbidden.set(true);
          this.error.set(null);
        } else {
          this.forbidden.set(false);
          this.error.set(apiError(err, `Could not load ${username}'s friends.`));
        }
      },
    });
  }
}
