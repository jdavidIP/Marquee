import { Component, ElementRef, computed, effect, inject, input, signal, untracked, viewChild } from '@angular/core';
import { DatePipe, DecimalPipe } from '@angular/common';
import { FormsModule } from '@angular/forms';
import { RouterLink } from '@angular/router';
import { AuthService } from '../../core/auth/auth.service';
import { FriendsService } from '../../core/api/friends.service';
import { UsersService } from '../../core/api/users.service';
import { PremiereHistoryService } from '../../core/api/premiere-history.service';
import { apiError } from '../../core/api/http-error';
import { FullProfileDto, PremiereHistoryEntryDto, ProfileDto, UserDto, isFullProfile } from '../../core/models';
import { AccessCategory, accessCategoryFor, nextAccessCategory } from '../../core/util/access-category';
import { IdBadgeComponent } from '../../shared/id-badge.component';
import { EmblemTicketComponent } from '../../shared/emblem-ticket.component';
import { AvatarCropDialogComponent } from '../../shared/avatar-crop-dialog.component';

/** The badge's recent-activity strip shows the last four, per the design handoff (issue #59). */
const RECENT_ACTIVITY_COUNT = 4;

@Component({
  selector: 'app-profile',
  standalone: true,
  imports: [DatePipe, DecimalPipe, FormsModule, RouterLink, IdBadgeComponent, EmblemTicketComponent, AvatarCropDialogComponent],
  templateUrl: './profile.component.html',
  styleUrl: './profile.component.css',
})
export class ProfileComponent {
  private readonly users = inject(UsersService);
  private readonly friends = inject(FriendsService);
  private readonly auth = inject(AuthService);
  private readonly premiereHistory = inject(PremiereHistoryService);

  /** Bound from the route, so /u/:username is a real address someone can link to or reload. */
  readonly username = input.required<string>();

  protected readonly profile = signal<ProfileDto | null>(null);
  protected readonly loading = signal(true);
  protected readonly error = signal<string | null>(null);
  protected readonly busy = signal(false);

  /** The photo being cropped (#63); the crop dialog is open while this is set. */
  protected readonly cropFile = signal<File | null>(null);
  /** An upload or removal is in flight — the dialog's Save stays disabled, so one click is one upload. */
  protected readonly photoBusy = signal(false);
  private readonly photoInput = viewChild<ElementRef<HTMLInputElement>>('photoInput');

  /** Draft bio, separate from the saved one so cancelling can put the original back. */
  protected bioDraft = '';
  protected readonly editing = signal(false);
  protected readonly saved = signal(false);

  /** The last four Premieres this account contributed to. Empty for a limited payload. */
  protected readonly recentActivity = signal<PremiereHistoryEntryDto[]>([]);

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

  /** The profile badge's access category (issue #59) — null on a limited/unissued payload. */
  protected readonly category = computed<AccessCategory | null>(() => {
    const f = this.full();
    return f ? accessCategoryFor(f.premieresAttended) : null;
  });

  /** Self only in the template; null at Jury, where there is nothing further to work toward. */
  protected readonly nextCategory = computed<AccessCategory | null>(() => {
    const cat = this.category();
    return cat ? nextAccessCategory(cat) : null;
  });

  protected readonly nextCategoryRemaining = computed(() => {
    const f = this.full();
    const next = this.nextCategory();
    return f && next ? Math.max(0, next.min - f.premieresAttended) : 0;
  });

  protected readonly nextCategoryProgressPct = computed(() => {
    const f = this.full();
    const cat = this.category();
    const next = this.nextCategory();
    if (!f || !cat || !next) return 0;
    return Math.min(100, ((f.premieresAttended - cat.min) / (next.min - cat.min)) * 100);
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

  protected choosePhoto(): void {
    this.photoInput()?.nativeElement.click();
  }

  protected onPhotoChosen(event: Event): void {
    const input = event.target as HTMLInputElement;
    const file = input.files?.[0];
    // Cleared so choosing the same file again still fires a change.
    input.value = '';
    if (file) this.cropFile.set(file);
  }

  protected uploadPhoto(photo: Blob): void {
    if (this.photoBusy()) return;
    this.photoBusy.set(true);
    this.users.uploadAvatar(photo).subscribe({
      next: (user) => this.photoSaved(user),
      error: (err: unknown) => this.photoFailed(err, 'Could not save your photo.'),
    });
  }

  protected removePhoto(): void {
    if (this.photoBusy()) return;
    this.photoBusy.set(true);
    this.users.removeAvatar().subscribe({
      next: (user) => this.photoSaved(user),
      error: (err: unknown) => this.photoFailed(err, 'Could not remove your photo.'),
    });
  }

  // The cached user carries avatarUrl, so this is what changes the topbar face straight away.
  private photoSaved(user: UserDto): void {
    this.auth.applyProfileUpdate(user);
    this.photoBusy.set(false);
    this.cropFile.set(null);
    this.error.set(null);
    this.load(this.username());
  }

  // The dialog closes so the message, shown at the top of the page, is not hidden behind it.
  private photoFailed(err: unknown, fallback: string): void {
    this.photoBusy.set(false);
    this.cropFile.set(null);
    this.error.set(apiError(err, fallback));
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

  /**
   * Works from either payload shape: friendshipStatus and friendRequestOutgoing are on both
   * FullProfileDto and LimitedProfileDto now, precisely so this button still works on a private
   * stranger's profile instead of only being reachable on a public one.
   */
  protected addFriend(): void {
    const p = this.profile();
    if (!p) return;

    this.busy.set(true);
    this.friends.sendRequest(p.username).subscribe({
      next: () => this.load(this.username()),
      error: (err: unknown) => this.fail(err, `Could not send a request to ${p.username}.`),
    });
  }

  /**
   * What the viewer's relationship to this profile means in words. Read straight off the payload —
   * the server worked it out, and the two nullable fields together say more than either alone.
   * Reads profile() rather than full(): a limited payload carries these two fields too.
   */
  protected readonly relationship = computed(() => {
    const p = this.profile();
    if (!p || this.isSelf()) return null;

    if (p.friendshipStatus === 'Accepted') return 'Friends';
    if (p.friendshipStatus === 'Pending') {
      return p.friendRequestOutgoing ? 'Request sent' : 'Wants to be friends';
    }
    return null;
  });

  /** Only a stranger with no pending request in either direction can be added from here. */
  protected readonly canAdd = computed(
    () => this.profile() !== null && !this.isSelf() && this.profile()!.friendshipStatus === null,
  );

  /**
   * An incoming request cannot be accepted from this screen: the payload carries the relationship
   * but not the request's id, and accept is keyed on that. Rather than fetch the requests list just
   * to find it, point at the screen that owns them.
   */
  protected readonly linkToRequests = computed(
    () => this.profile()?.friendshipStatus === 'Pending' && this.profile()?.friendRequestOutgoing === false,
  );

  /** "40 – 99 Premieres" or, at Jury, "100+ Premieres" — there is no upper bound to print. */
  protected categoryRange(cat: AccessCategory): string {
    return cat.max === null ? `${cat.min}+ Premieres` : `${cat.min} – ${cat.max} Premieres`;
  }

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
    this.recentActivity.set([]);
    this.users.profile(username).subscribe({
      next: (p) => {
        this.profile.set(p);
        this.loading.set(false);
        this.busy.set(false);
        this.error.set(null);
        if (isFullProfile(p)) this.loadRecentActivity(username);
      },
      error: (err: unknown) => {
        this.profile.set(null);
        this.loading.set(false);
        this.busy.set(false);
        this.error.set(apiError(err, `Could not load ${username}.`));
      },
    });
  }

  /**
   * Reuses the premiere-history endpoint (issue #38) rather than a dedicated one: a Contribution
   * only ever exists once a Premiere has opened, so there is no "never opened" state to filter out
   * here — every entry the badge shows really happened.
   */
  private loadRecentActivity(username: string): void {
    this.premiereHistory
      .forUser(username, { sort: 'Opened', pageSize: RECENT_ACTIVITY_COUNT })
      .subscribe({
        next: (result) => this.recentActivity.set(result.items),
        error: () => this.recentActivity.set([]),
      });
  }
}
