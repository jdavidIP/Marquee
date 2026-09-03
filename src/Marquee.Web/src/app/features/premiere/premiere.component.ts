import { Component, DestroyRef, OnDestroy, OnInit, computed, inject, signal } from '@angular/core';
import { takeUntilDestroyed } from '@angular/core/rxjs-interop';
import { DatePipe, DecimalPipe } from '@angular/common';
import { RouterLink } from '@angular/router';
import { AuthService } from '../../core/auth.service';
import { AnonymousSessionService } from '../../core/anonymous-session.service';
import { PremiereService } from '../../core/premiere.service';
import { RealtimeService } from '../../core/realtime.service';
import { ClapResponse, LobbyDto, PremiereDto } from '../../core/models';
import { initialsOf, monogramColor } from '../../core/avatar';
import { environment } from '../../../environments/environment';

/** Bulbs around the marquee frame, one row top and bottom. */
const BULB_COUNT = 22;
/** How long after a reveal to re-fetch once, to pick up MyEmblemTier if the Worker had not
 *  assigned it yet at the moment of reveal (it does so asynchronously, not in the clap path). */
const EMBLEM_SETTLE_DELAY_MS = 1500;

const isOpenStatus = (status: string | undefined): boolean =>
  status === 'Opened' || status === 'AutoOpened';

interface Face {
  userId: string;
  initials: string;
  avatarUrl: string | null;
  isFriend: boolean;
  bg: string;
}

/** "Ada, Miles and Rosa" — no Oxford comma, capped to the first three named. */
function joinNames(names: string[]): string {
  const shown = names.slice(0, 3);
  if (shown.length <= 1) return shown[0] ?? '';
  return `${shown.slice(0, -1).join(', ')} and ${shown[shown.length - 1]}`;
}

function formatClock(totalSeconds: number): string {
  const m = Math.floor(totalSeconds / 60);
  const s = totalSeconds % 60;
  return `${String(m).padStart(2, '0')}:${String(s).padStart(2, '0')}`;
}

@Component({
  selector: 'app-premiere',
  imports: [RouterLink, DecimalPipe, DatePipe],
  templateUrl: './premiere.component.html',
  styleUrl: './premiere.component.css',
})
export class PremiereComponent implements OnInit, OnDestroy {
  protected readonly auth = inject(AuthService);
  protected readonly realtime = inject(RealtimeService);
  private readonly anon = inject(AnonymousSessionService);
  private readonly premieres = inject(PremiereService);
  private readonly destroyRef = inject(DestroyRef);

  protected readonly premiere = signal<PremiereDto | null>(null);
  protected readonly nextPremiere = signal<PremiereDto | null>(null);
  protected readonly loading = signal(true);
  protected readonly clapping = signal(false);
  protected readonly error = signal<string | null>(null);
  protected readonly lobby = signal<LobbyDto | null>(null);
  private readonly nowMs = signal(Date.now());

  /**
   * The single value the marquee is drawn from. The curtain gap and the lit-bulb count are both
   * derived from it — neither is stored, so they can never disagree with each other or with the
   * count. Once a Premiere has opened the curtain is all the way up regardless of the final tally,
   * because an auto-opened Premiere reveals its movie without ever reaching the threshold (§4.5).
   */
  protected readonly progress = computed(() => {
    const p = this.premiere();
    if (!p) return 0;
    if (isOpenStatus(p.status)) return 1;
    if (p.threshold <= 0) return 0;
    return Math.min(1, p.totalClaps / p.threshold);
  });

  protected readonly progressPct = computed(() => Math.round(this.progress() * 100));

  /** The curtain lags the count on purpose — full open is reserved for the reveal itself. */
  protected readonly curtainTravelPct = computed(() =>
    this.isOpen() ? 100 : Math.min(56, this.progressPct() * 0.56),
  );

  protected readonly bulbs = computed(() => {
    const ratio = this.isOpen() ? 1 : Math.max(0.06, this.progress());
    return Array.from({ length: BULB_COUNT }, (_, i) => ({
      lit: (i + 1) / BULB_COUNT <= ratio,
      delay: `${((i % 8) * 0.14).toFixed(2)}s`,
    }));
  });

  protected readonly isOpen = computed(() => isOpenStatus(this.premiere()?.status));

  /** myCap, not registeredClapCap — an anonymous participant's cap is lower (§4.2). */
  protected readonly capReached = computed(() => {
    const p = this.premiere();
    return !!p && p.myClaps >= p.myCap;
  });

  protected readonly remainingSeconds = computed(() => {
    const p = this.premiere();
    if (!p?.expiresAt || this.isOpen()) return 0;
    const ms = new Date(p.expiresAt).getTime() - this.nowMs();
    return Math.max(0, Math.round(ms / 1000));
  });

  protected readonly timerLabel = computed(() =>
    this.isOpen() ? 'Opened' : formatClock(this.remainingSeconds()),
  );

  protected readonly statusText = computed(() => {
    const p = this.premiere();
    if (!p) return '';
    if (this.isOpen())
      return p.status === 'AutoOpened' ? 'Premiere opened on the timer' : 'Premiere opened';
    return `Premiere live · ${p.scopeId}`;
  });

  protected readonly kicker = computed(() => (this.isOpen() ? 'Now showing' : 'Coming up'));

  protected readonly signCaption = computed(() => {
    const p = this.premiere();
    if (!p) return '';
    return this.isOpen()
      ? "Now showing · added to every clapper's library"
      : 'Clap to open the curtain';
  });

  /** Movie title split into per-letter tiles, grouped by word so a wrap never splits a word. */
  protected readonly titleWords = computed(() => {
    const title = this.premiere()?.movie?.title;
    return title ? title.split(' ').map((word) => ({ word, letters: [...word] })) : [];
  });

  protected readonly showFaces = computed(() => !this.isOpen() && this.faces().length > 0);

  protected readonly faces = computed<Face[]>(() =>
    (this.lobby()?.faces ?? []).map((f) => ({
      userId: f.userId,
      initials: f.avatarUrl ? '' : initialsOf(f.username),
      avatarUrl: f.avatarUrl,
      isFriend: f.isFriend,
      bg: monogramColor(f.userId),
    })),
  );

  protected readonly lobbyLabel = computed(() =>
    this.isOpen() ? 'Who opened it' : 'In the lobby',
  );

  /**
   * The strip's crowd note. Friends in the lobby sample are named (capped to three); the anonymous
   * count always comes from the lobby endpoint's real tally, not a guess — never color/text alone
   * (CLAUDE.md's convention carried from the prior system applies to this line too).
   */
  protected readonly crowdNote = computed(() => {
    const p = this.premiere();
    const l = this.lobby();
    if (!p || !l) return '';

    if (this.isOpen()) {
      const who = p.contributors === 1 ? 'person opened' : 'people opened';
      const friendCount = l.faces.filter((f) => f.isFriend).length;
      return friendCount > 0
        ? `${p.contributors.toLocaleString()} ${who} this Premiere together, ${friendCount} of them your friends.`
        : `${p.contributors.toLocaleString()} ${who} this Premiere together.`;
    }

    const friendNames = l.faces.filter((f) => f.isFriend).map((f) => f.username);
    const base =
      friendNames.length > 0
        ? `${joinNames(friendNames)} ${friendNames.length === 1 ? 'is' : 'are'} clapping`
        : `${l.registeredCount.toLocaleString()} ${l.registeredCount === 1 ? 'person is' : 'people are'} clapping`;

    return l.anonymousCount > 0
      ? `${base} · ${l.anonymousCount.toLocaleString()} more in the crowd clapped anonymously`
      : base;
  });

  protected readonly pips = computed(() => {
    const p = this.premiere();
    return p ? Array.from({ length: p.myCap }, (_, i) => i < p.myClaps) : [];
  });

  protected readonly clapButtonState = computed<'on' | 'capped' | 'off'>(() => {
    if (!this.premiere() || this.isOpen()) return 'off';
    return this.capReached() ? 'capped' : 'on';
  });

  protected readonly clapButtonLabel = computed(() => {
    if (!this.premiere()) return 'No Premiere live';
    if (this.isOpen()) return this.auth.isLoggedIn() ? 'In your library' : 'Nothing kept';
    return this.capReached() ? 'You are capped' : 'Clap';
  });

  protected readonly capNote = computed(() => {
    const p = this.premiere();
    if (!p || this.isOpen()) return '';
    return this.capReached()
      ? `You have spent your cap of ${p.myCap} claps — the rest is up to the room`
      : `Cap of ${p.myCap} claps per person, so no one opens a Premiere alone`;
  });

  private pollHandle: ReturnType<typeof setInterval> | null = null;
  private clockHandle: ReturnType<typeof setInterval> | null = null;
  private lobbyPollHandle: ReturnType<typeof setInterval> | null = null;
  private emblemSettleHandle: ReturnType<typeof setTimeout> | null = null;

  /**
   * Whether this viewer can clap at all — signed in, or holding a valid anonymous session (§4.2).
   * Not auth.isLoggedIn() alone: a signed-out visitor is a participant too, just capped lower and
   * earning nothing kept.
   */
  protected readonly canParticipate = computed(() => this.auth.isLoggedIn() || !!this.anon.session());

  ngOnInit(): void {
    this.subscribeToRealtime();

    // The very first getActive() must not race ensure() — a request that goes out before the
    // anonymous session exists carries no identity at all, and would render with the registered
    // cap instead of the anonymous one until whatever happens to refresh it next.
    void this.initialLoad();

    // Fallback only — the socket is the primary path, so this ticks slowly and skips entirely
    // while the connection is healthy.
    this.pollHandle = setInterval(() => {
      if (!this.realtime.connected() && !this.isOpen()) this.load(false);
    }, environment.fallbackPollIntervalMs);

    this.clockHandle = setInterval(() => this.nowMs.set(Date.now()), 1000);
  }

  private async initialLoad(): Promise<void> {
    if (!this.auth.isLoggedIn()) {
      // Best-effort: the issuing endpoint is rate-limited by IP and can legitimately fail, in
      // which case canParticipate() just stays false and the screen degrades to watch-only.
      await this.anon.ensure();
    }
    this.load(true);
  }

  ngOnDestroy(): void {
    if (this.pollHandle) clearInterval(this.pollHandle);
    if (this.clockHandle) clearInterval(this.clockHandle);
    this.stopLobbyPolling();
    if (this.emblemSettleHandle) clearTimeout(this.emblemSettleHandle);
    void this.realtime.stopWatching();
  }

  private subscribeToRealtime(): void {
    void this.realtime.connect();

    this.realtime.clapUpdates.pipe(takeUntilDestroyed(this.destroyRef)).subscribe((u) => {
      this.premiere.update((cur) =>
        cur && cur.id === u.premiereId
          ? {
              ...cur,
              totalClaps: u.totalClaps,
              threshold: u.threshold,
              contributors: u.contributors,
            }
          : cur,
      );
    });

    this.realtime.premiereOpened.pipe(takeUntilDestroyed(this.destroyRef)).subscribe((n) => {
      this.premiere.update((cur) =>
        cur && cur.id === n.premiereId
          ? {
              ...cur,
              status: n.status as PremiereDto['status'],
              totalClaps: n.totalClaps,
              contributors: n.contributors,
              openedAt: n.openedAt,
              movie: n.movie ?? cur.movie,
            }
          : cur,
      );
      this.stopLobbyPolling();
      this.scheduleEmblemSettle();
      // The Premiere the viewer just watched is over; find out what is next.
      this.loadNext();
    });

    this.realtime.premiereActivated.pipe(takeUntilDestroyed(this.destroyRef)).subscribe((p) => {
      // A new Premiere went live while this page was open — switch to it.
      this.nextPremiere.set(null);
      this.premiere.set(p);
      this.error.set(null);
      this.lobby.set(null);
      void this.realtime.watchPremiere(p.id);
      this.startLobbyPolling();
    });
  }

  private load(initial: boolean): void {
    this.premieres.getActive().subscribe({
      next: (p) => {
        this.premiere.set(p);
        this.nextPremiere.set(null);
        this.loading.set(false);
        void this.realtime.watchPremiere(p.id);
        if (isOpenStatus(p.status)) {
          this.stopLobbyPolling();
        } else {
          this.startLobbyPolling();
        }
      },
      error: (err: unknown) => {
        this.loading.set(false);
        const status = (err as { status?: number }).status;
        if (status === 404) {
          // No active Premiere right now. Keep any already-revealed one on screen.
          if (initial) {
            this.premiere.set(null);
            this.loadNext();
          }
        } else {
          this.error.set('Could not load the Premiere.');
        }
      },
    });
  }

  private loadNext(): void {
    this.premieres.getNext().subscribe({
      next: (p) => this.nextPremiere.set(p),
      error: () => this.nextPremiere.set(null),
    });
  }

  private startLobbyPolling(): void {
    this.stopLobbyPolling();
    this.fetchLobby();
    this.lobbyPollHandle = setInterval(() => this.fetchLobby(), environment.lobbyPollIntervalMs);
  }

  private stopLobbyPolling(): void {
    if (this.lobbyPollHandle) {
      clearInterval(this.lobbyPollHandle);
      this.lobbyPollHandle = null;
    }
  }

  private fetchLobby(): void {
    const p = this.premiere();
    if (!p) return;
    this.premieres.lobby(p.id).subscribe({
      next: (l) => this.lobby.set(l),
      // 404 once the Premiere is no longer live — harmless, the poll is about to be stopped anyway.
      error: () => {},
    });
  }

  /** Backfills MyEmblemTier once, in case the Worker had not assigned it yet at reveal time. */
  private scheduleEmblemSettle(): void {
    if (this.emblemSettleHandle) return;
    this.emblemSettleHandle = setTimeout(() => {
      this.emblemSettleHandle = null;
      const p = this.premiere();
      if (p && p.myEmblemTier == null && isOpenStatus(p.status)) {
        this.premieres.get(p.id).subscribe((fresh) => this.premiere.set(fresh));
      }
    }, EMBLEM_SETTLE_DELAY_MS);
  }

  protected clap(): void {
    const p = this.premiere();
    if (!p || this.clapping() || this.isOpen()) return;
    this.clapping.set(true);

    this.premieres.clap(p.id).subscribe({
      next: (r: ClapResponse) => {
        this.clapping.set(false);
        // The broadcast carries the shared count; this response carries what only this viewer
        // knows — how many of their own claps landed. Personalised data never goes to a group.
        this.premiere.update((cur) =>
          cur
            ? {
                ...cur,
                totalClaps: Math.max(cur.totalClaps, r.totalClaps),
                myClaps: r.myClaps,
                status: r.status as PremiereDto['status'],
                movie: r.movie ?? cur.movie,
              }
            : cur,
        );
        if (isOpenStatus(r.status)) {
          this.stopLobbyPolling();
          this.scheduleEmblemSettle();
        }
      },
      error: (err: unknown) => {
        this.clapping.set(false);
        const status = (err as { status?: number }).status;
        this.error.set(
          status === 409
            ? 'This Premiere is no longer accepting claps.'
            : 'That clap did not register. Try again.',
        );
      },
    });
  }
}
