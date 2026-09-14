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
import { RouterLink } from '@angular/router';
import { UsersService } from '../../core/users.service';
import { FriendsService } from '../../core/friends.service';
import { apiError, isForbidden } from '../../core/http-error';
import { FriendDto, ProfileDto } from '../../core/models';
import { AuthService } from '../../core/auth.service';
import { initialsOf, monogramColor } from '../../core/avatar';

const SEARCH_DEBOUNCE_MS = 300;

/**
 * Someone else's friend list is read-only (issue #65 tracks giving it real relationship actions —
 * add/accept/etc per row — since that needs a backend field this screen's own API doesn't have).
 * Your own list is the one exception: Remove has always belonged here, not on the search page,
 * which is why #60 pulled its inline copy out in favour of this screen.
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
  private readonly friendsApi = inject(FriendsService);
  private readonly auth = inject(AuthService);

  protected readonly initialsOf = initialsOf;
  protected readonly monogramColor = monogramColor;

  /** Bound from the route, so /u/:username/friends is a real, linkable, reloadable address. */
  readonly username = input.required<string>();

  protected readonly friends = signal<FriendDto[]>([]);
  protected readonly loading = signal(true);
  protected readonly error = signal<string | null>(null);
  protected readonly forbidden = signal(false);

  /** Set only from an unfiltered load, so a search narrowing the visible rows never moves it. */
  protected readonly totalCount = signal(0);

  protected readonly query = signal('');
  private searchTimer: ReturnType<typeof setTimeout> | null = null;

  /** Only the row being acted on is disabled; removing one friend must not freeze the whole list. */
  protected readonly askingId = signal<string | null>(null);
  protected readonly busyId = signal<string | null>(null);
  /** Plain confirmation text after a removal — no Undo: RemoveFriendAsync is a hard delete with no
   *  grace period, so an "Undo" that only sent a fresh (pending) request would lie about what it did. */
  protected readonly removedNotice = signal<string | null>(null);

  /**
   * The viewer's own relationship to a *private* account they are forbidden from — fetched only
   * then, from the same endpoint the profile page reads, so the private-account refusal can offer
   * the correct action (Add friend / already pending / already friends) instead of guessing.
   */
  protected readonly theirProfile = signal<ProfileDto | null>(null);
  protected readonly addBusy = signal(false);

  protected readonly isSelf = computed(() => this.username() === this.auth.user()?.username);

  /** Same wording and gating as ProfileComponent's own relationship/canAdd/linkToRequests. */
  protected readonly relationship = computed(() => {
    const p = this.theirProfile();
    if (!p) return null;
    if (p.friendshipStatus === 'Accepted') return 'Friends';
    if (p.friendshipStatus === 'Pending') {
      return p.friendRequestOutgoing ? 'Request sent' : 'Wants to be friends';
    }
    return null;
  });

  protected readonly canAdd = computed(() => this.theirProfile()?.friendshipStatus === null);

  /**
   * An incoming request cannot be accepted from this screen: the payload carries the relationship
   * but not the request's id, and accept is keyed on that. Points at the screen that owns it instead
   * of fetching the requests list just to find one id.
   */
  protected readonly linkToRequests = computed(
    () =>
      this.theirProfile()?.friendshipStatus === 'Pending' &&
      this.theirProfile()?.friendRequestOutgoing === false,
  );

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
    this.searchTimer = setTimeout(
      () => this.load(this.username(), value.trim()),
      SEARCH_DEBOUNCE_MS,
    );
  }

  protected clearSearch(): void {
    if (this.searchTimer) clearTimeout(this.searchTimer);
    this.query.set('');
    this.load(this.username(), '');
  }

  protected ask(userId: string): void {
    this.askingId.set(userId);
    this.error.set(null);
  }

  protected cancelAsk(): void {
    this.askingId.set(null);
  }

  protected confirmRemove(friend: FriendDto): void {
    this.askingId.set(null);
    this.busyId.set(friend.userId);
    this.friendsApi.remove(friend.userId).subscribe({
      next: () => {
        this.busyId.set(null);
        this.friends.update((list) => list.filter((f) => f.userId !== friend.userId));
        this.totalCount.update((n) => Math.max(0, n - 1));
        this.removedNotice.set(`You and ${friend.username} are no longer friends.`);
      },
      error: (err: unknown) => {
        this.busyId.set(null);
        this.error.set(apiError(err, `Could not remove ${friend.username}.`));
      },
    });
  }

  protected addFriend(): void {
    const username = this.username();
    this.addBusy.set(true);
    this.friendsApi.sendRequest(username).subscribe({
      next: () => this.loadTheirProfile(username),
      error: (err: unknown) => {
        this.addBusy.set(false);
        this.error.set(apiError(err, `Could not send a request to ${username}.`));
      },
    });
  }

  private loadTheirProfile(username: string): void {
    this.users.profile(username).subscribe({
      next: (p) => {
        this.theirProfile.set(p);
        this.addBusy.set(false);
      },
      error: () => this.addBusy.set(false),
    });
  }

  private load(username: string, search: string): void {
    this.loading.set(true);
    this.removedNotice.set(null);
    this.users.friendsOf(username, search).subscribe({
      next: (friends) => {
        this.friends.set(friends);
        if (!search) this.totalCount.set(friends.length);
        this.loading.set(false);
        this.forbidden.set(false);
        this.theirProfile.set(null);
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
          this.loadTheirProfile(username);
        } else {
          this.forbidden.set(false);
          this.error.set(apiError(err, `Could not load ${username}'s friends.`));
        }
      },
    });
  }
}
