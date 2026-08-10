import { Component, OnDestroy, OnInit, computed, inject, signal } from '@angular/core';
import { DatePipe } from '@angular/common';
import { RouterLink } from '@angular/router';
import { Observable, forkJoin } from 'rxjs';
import { FriendsService } from '../../core/friends.service';
import { UsersService } from '../../core/users.service';
import { AuthService } from '../../core/auth.service';
import { apiError } from '../../core/http-error';
import { FriendDto, FriendRequestDto, UserSearchResultDto } from '../../core/models';
import { ConfirmDialogComponent } from '../../shared/confirm-dialog.component';

const SEARCH_DEBOUNCE_MS = 300;

/**
 * How the signed-in user already relates to a search hit. Computed from the lists this screen has
 * already loaded, so the button says what will happen instead of letting someone discover it by
 * rejection — the same reasoning behind the admin editors showing their bounds up front.
 */
type Relation = 'self' | 'friend' | 'requested' | 'incoming' | 'none';

interface SearchRow {
  user: UserSearchResultDto;
  relation: Relation;
  /** Set when relation is 'incoming', so the request can be accepted straight from the results. */
  requestId: string | null;
}

@Component({
  selector: 'app-friends',
  standalone: true,
  imports: [DatePipe, RouterLink, ConfirmDialogComponent],
  templateUrl: './friends.component.html',
  styleUrl: './friends.component.css',
})
export class FriendsComponent implements OnInit, OnDestroy {
  private readonly friendsApi = inject(FriendsService);
  private readonly users = inject(UsersService);
  private readonly auth = inject(AuthService);

  protected readonly friends = signal<FriendDto[]>([]);
  protected readonly requests = signal<FriendRequestDto[]>([]);
  protected readonly loading = signal(true);
  protected readonly error = signal<string | null>(null);

  protected readonly query = signal('');
  protected readonly results = signal<UserSearchResultDto[]>([]);
  protected readonly searching = signal(false);
  /** Distinguishes "nothing matched" from "nothing searched for yet". */
  protected readonly searched = signal(false);

  /** Only the row being acted on is disabled; one action must not freeze the whole screen. */
  protected readonly busyId = signal<string | null>(null);
  protected readonly pendingRemoval = signal<FriendDto | null>(null);

  protected readonly incoming = computed(() => this.requests().filter((r) => !r.outgoing));

  /**
   * Read-only by design: the API has no withdraw endpoint — accept and reject are addressee-only,
   * and removing a friendship matches only accepted ones. Showing a cancel button the server cannot
   * honour would be worse than showing none.
   */
  protected readonly outgoing = computed(() => this.requests().filter((r) => r.outgoing));

  protected readonly searchRows = computed<SearchRow[]>(() => {
    const myId = this.auth.user()?.id;
    const friendIds = new Set(this.friends().map((f) => f.userId));
    const pending = new Map(this.requests().map((r) => [r.userId, r]));

    return this.results().map((user) => {
      if (user.id === myId) return { user, relation: 'self' as const, requestId: null };
      if (friendIds.has(user.id)) return { user, relation: 'friend' as const, requestId: null };

      const request = pending.get(user.id);
      if (request) {
        return request.outgoing
          ? { user, relation: 'requested' as const, requestId: request.id }
          : { user, relation: 'incoming' as const, requestId: request.id };
      }

      return { user, relation: 'none' as const, requestId: null };
    });
  });

  private searchTimer: ReturnType<typeof setTimeout> | null = null;

  ngOnInit(): void {
    this.reload(true);
  }

  /** A pending debounce would otherwise fire a search against a component nobody is looking at. */
  ngOnDestroy(): void {
    if (this.searchTimer) clearTimeout(this.searchTimer);
  }

  protected onSearchInput(value: string): void {
    this.query.set(value);
    if (this.searchTimer) clearTimeout(this.searchTimer);

    if (!value.trim()) {
      this.results.set([]);
      this.searched.set(false);
      return;
    }

    // One request per pause, not one per keystroke — /api/users is also IP-rate-limited.
    this.searchTimer = setTimeout(() => this.runSearch(value.trim()), SEARCH_DEBOUNCE_MS);
  }

  protected clearSearch(): void {
    if (this.searchTimer) clearTimeout(this.searchTimer);
    this.query.set('');
    this.results.set([]);
    this.searched.set(false);
  }

  protected send(username: string, id: string): void {
    this.act(id, this.friendsApi.sendRequest(username), `Could not send a request to ${username}.`);
  }

  /** Takes the id and name rather than the DTO, so the search results can accept without one. */
  protected accept(requestId: string, username: string): void {
    this.act(requestId, this.friendsApi.accept(requestId), `Could not accept ${username}.`);
  }

  protected reject(requestId: string, username: string): void {
    this.act(requestId, this.friendsApi.reject(requestId), `Could not reject ${username}.`);
  }

  protected askToRemove(friend: FriendDto): void {
    this.pendingRemoval.set(friend);
  }

  protected confirmRemoval(): void {
    const friend = this.pendingRemoval();
    if (!friend) return;

    this.busyId.set(friend.userId);
    this.friendsApi.remove(friend.userId).subscribe({
      next: () => {
        this.pendingRemoval.set(null);
        this.finish();
      },
      error: (err: unknown) => {
        this.pendingRemoval.set(null);
        this.fail(err, `Could not remove ${friend.username}.`);
      },
    });
  }

  protected relationLabel(relation: Relation): string {
    switch (relation) {
      case 'self':
        return 'This is you';
      case 'friend':
        return 'Already friends';
      case 'requested':
        return 'Request sent';
      default:
        return '';
    }
  }

  /**
   * Every mutation re-reads both lists rather than patching one.
   *
   * Not caution for its own sake: the server resolves the reciprocal case itself — asking someone
   * who already asked you accepts their request instead of opening a second one — so a single
   * response cannot tell this screen whether it now holds a pending request or a new friendship.
   * Re-reading is the only thing that is right in both cases.
   */
  private act(id: string, call: Observable<unknown>, failure: string): void {
    this.busyId.set(id);
    call.subscribe({
      next: () => this.finish(),
      error: (err: unknown) => this.fail(err, failure),
    });
  }

  private finish(): void {
    this.error.set(null);
    this.reload(false);
  }

  private fail(err: unknown, fallback: string): void {
    this.busyId.set(null);
    this.error.set(apiError(err, fallback));
    // The screen disagreeing with the server is what produced this, so re-read rather than guess.
    this.reload(false);
  }

  private runSearch(query: string): void {
    this.searching.set(true);
    this.users.search(query).subscribe({
      next: (found) => {
        this.results.set(found);
        this.searching.set(false);
        this.searched.set(true);
      },
      error: (err: unknown) => {
        this.searching.set(false);
        this.searched.set(true);
        this.error.set(apiError(err, 'Could not search for users.'));
      },
    });
  }

  private reload(first: boolean): void {
    if (first) this.loading.set(true);

    forkJoin({ friends: this.friendsApi.friends(), requests: this.friendsApi.requests() }).subscribe({
      next: ({ friends, requests }) => {
        this.friends.set(friends);
        this.requests.set(requests);
        this.loading.set(false);
        this.busyId.set(null);
      },
      error: (err: unknown) => {
        this.loading.set(false);
        this.busyId.set(null);
        // Existing rows stay on screen: losing them because one refresh failed removes what the
        // person came here to read.
        this.error.set(apiError(err, 'Could not load your friends.'));
      },
    });
  }
}
