import { Component, computed, effect, inject, input, signal, untracked } from '@angular/core';
import { DatePipe, DecimalPipe } from '@angular/common';
import { FormsModule } from '@angular/forms';
import { RouterLink } from '@angular/router';
import { AuthService } from '../../core/auth.service';
import { FriendsService } from '../../core/friends.service';
import { UsersService } from '../../core/users.service';
import { apiError } from '../../core/http-error';
import { FullProfileDto, ProfileDto, isFullProfile } from '../../core/models';

@Component({
  selector: 'app-profile',
  standalone: true,
  imports: [DatePipe, DecimalPipe, FormsModule, RouterLink],
  templateUrl: './profile.component.html',
  styleUrl: './profile.component.css',
})
export class ProfileComponent {
  private readonly users = inject(UsersService);
  private readonly friends = inject(FriendsService);
  private readonly auth = inject(AuthService);

  /** Bound from the route, so /u/:username is a real address someone can link to or reload. */
  readonly username = input.required<string>();

  protected readonly profile = signal<ProfileDto | null>(null);
  protected readonly loading = signal(true);
  protected readonly error = signal<string | null>(null);
  protected readonly busy = signal(false);

  /** Draft bio, separate from the saved one so cancelling can put the original back. */
  protected bioDraft = '';
  protected readonly editing = signal(false);
  protected readonly saved = signal(false);

  /**
   * Whichever shape the server sent, narrowed for the template. A limited payload has no id, no
   * counts, and no relationship — the fields are absent, not null — so everything below reads
   * through this rather than assuming the full shape.
   */
  protected readonly full = computed<FullProfileDto | null>(() => {
    const p = this.profile();
    return p && isFullProfile(p) ? p : null;
  });

  /** True when the server sent the smaller payload, which only happens to a stranger. */
  protected readonly limited = computed(() => {
    const p = this.profile();
    return p !== null && !isFullProfile(p);
  });

  protected readonly isSelf = computed(() => {
    const f = this.full();
    return f !== null && f.id === this.auth.user()?.id;
  });

  constructor() {
    // One code path loads the profile — the route param changing — so a link, a reload and an
    // in-app navigation between two profiles all arrive the same way. Untracked for the same
    // reason as the admin users screen: the load writes the signals it would otherwise depend on.
    effect(() => {
      const name = this.username();
      untracked(() => this.load(name));
    });
  }

  protected startEditing(): void {
    this.bioDraft = this.full()?.bio ?? '';
    this.saved.set(false);
    this.editing.set(true);
  }

  protected cancelEditing(): void {
    this.editing.set(false);
  }

  protected saveBio(): void {
    const bio = this.bioDraft.trim();
    this.patch({ bio: bio.length > 0 ? bio : null }, () => this.editing.set(false));
  }

  /**
   * Privacy flips immediately rather than through the editor: it is a single switch with no draft
   * state, and hiding it behind Save/Cancel would suggest otherwise.
   */
  protected togglePrivacy(): void {
    const f = this.full();
    if (!f) return;
    this.patch({ isPrivate: !f.isPrivate });
  }

  protected addFriend(): void {
    const f = this.full();
    if (!f) return;

    this.busy.set(true);
    this.friends.sendRequest(f.username).subscribe({
      next: () => this.load(this.username()),
      error: (err: unknown) => this.fail(err, `Could not send a request to ${f.username}.`),
    });
  }

  /**
   * What the viewer's relationship to this profile means in words. Read straight off the payload —
   * the server worked it out, and the two nullable fields together say more than either alone.
   */
  protected readonly relationship = computed(() => {
    const f = this.full();
    if (!f || this.isSelf()) return null;

    if (f.friendshipStatus === 'Accepted') return 'Friends';
    if (f.friendshipStatus === 'Pending') {
      return f.friendRequestOutgoing ? 'Request sent' : 'Wants to be friends';
    }
    return null;
  });

  /** Only a stranger with no pending request in either direction can be added from here. */
  protected readonly canAdd = computed(
    () => this.full() !== null && !this.isSelf() && this.full()!.friendshipStatus === null,
  );

  /**
   * An incoming request cannot be accepted from this screen: the payload carries the relationship
   * but not the request's id, and accept is keyed on that. Rather than fetch the requests list just
   * to find it, point at the screen that owns them.
   */
  protected readonly linkToRequests = computed(
    () => this.full()?.friendshipStatus === 'Pending' && this.full()?.friendRequestOutgoing === false,
  );

  private patch(request: { bio?: string | null; isPrivate?: boolean | null }, done?: () => void): void {
    this.busy.set(true);
    this.users.updateMe(request).subscribe({
      next: (user) => {
        // The cached user carries bio and isPrivate, so a stale copy would leave the topbar and any
        // later read disagreeing with what was just saved.
        this.auth.applyProfileUpdate(user);
        this.busy.set(false);
        this.error.set(null);
        this.saved.set(true);
        done?.();
        this.load(this.username());
      },
      error: (err: unknown) => this.fail(err, 'Could not save your profile.'),
    });
  }

  private fail(err: unknown, fallback: string): void {
    this.busy.set(false);
    this.error.set(apiError(err, fallback));
  }

  private load(username: string): void {
    this.loading.set(true);
    this.users.profile(username).subscribe({
      next: (p) => {
        this.profile.set(p);
        this.loading.set(false);
        this.busy.set(false);
        this.error.set(null);
      },
      error: (err: unknown) => {
        this.profile.set(null);
        this.loading.set(false);
        this.busy.set(false);
        this.error.set(apiError(err, `Could not load ${username}.`));
      },
    });
  }
}
