import { Component, DestroyRef, OnDestroy, OnInit, computed, inject, signal } from '@angular/core';
import { takeUntilDestroyed } from '@angular/core/rxjs-interop';
import { DatePipe, DecimalPipe } from '@angular/common';
import { RouterLink } from '@angular/router';
import { AuthService } from '../../core/auth.service';
import { PremiereService } from '../../core/premiere.service';
import { RealtimeService } from '../../core/realtime.service';
import { ClapResponse, PremiereDto } from '../../core/models';
import { environment } from '../../../environments/environment';

/** Bulbs around the marquee frame. Lit count is derived from progress, never tracked separately. */
const BULB_COUNT = 24;

const isOpenStatus = (status: string | undefined): boolean =>
  status === 'Opened' || status === 'AutoOpened';

@Component({
  selector: 'app-premiere',
  imports: [RouterLink, DecimalPipe, DatePipe],
  templateUrl: './premiere.component.html',
  styleUrl: './premiere.component.css',
})
export class PremiereComponent implements OnInit, OnDestroy {
  protected readonly auth = inject(AuthService);
  protected readonly realtime = inject(RealtimeService);
  private readonly premieres = inject(PremiereService);
  private readonly destroyRef = inject(DestroyRef);

  protected readonly premiere = signal<PremiereDto | null>(null);
  protected readonly nextPremiere = signal<PremiereDto | null>(null);
  protected readonly loading = signal(true);
  protected readonly clapping = signal(false);
  protected readonly error = signal<string | null>(null);

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

  /** How far each curtain panel has slid off-stage, as a percentage of its own width. */
  protected readonly curtainTravelPct = computed(() => this.progress() * 100);

  protected readonly bulbs = computed(() => {
    const lit = Math.round(this.progress() * BULB_COUNT);
    return Array.from({ length: BULB_COUNT }, (_, i) => i < lit);
  });

  protected readonly isOpen = computed(() => isOpenStatus(this.premiere()?.status));

  protected readonly capReached = computed(() => {
    const p = this.premiere();
    return !!p && p.myClaps >= p.registeredClapCap;
  });

  private pollHandle: ReturnType<typeof setInterval> | null = null;

  ngOnInit(): void {
    this.load(true);
    this.subscribeToRealtime();

    // Fallback only — the socket is the primary path, so this ticks slowly and skips entirely
    // while the connection is healthy.
    this.pollHandle = setInterval(() => {
      if (!this.realtime.connected() && !this.isOpen()) this.load(false);
    }, environment.fallbackPollIntervalMs);
  }

  ngOnDestroy(): void {
    if (this.pollHandle) clearInterval(this.pollHandle);
    void this.realtime.stopWatching();
  }

  private subscribeToRealtime(): void {
    void this.realtime.connect();

    this.realtime.clapUpdates.pipe(takeUntilDestroyed(this.destroyRef)).subscribe((u) => {
      this.premiere.update((cur) =>
        cur && cur.id === u.premiereId
          ? { ...cur, totalClaps: u.totalClaps, threshold: u.threshold, contributors: u.contributors }
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
      // The Premiere the viewer just watched is over; find out what is next.
      this.loadNext();
    });

    this.realtime.premiereActivated.pipe(takeUntilDestroyed(this.destroyRef)).subscribe((p) => {
      // A new Premiere went live while this page was open — switch to it.
      this.nextPremiere.set(null);
      this.premiere.set(p);
      this.error.set(null);
      void this.realtime.watchPremiere(p.id);
    });
  }

  private load(initial: boolean): void {
    this.premieres.getActive().subscribe({
      next: (p) => {
        this.premiere.set(p);
        this.nextPremiere.set(null);
        this.loading.set(false);
        void this.realtime.watchPremiere(p.id);
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

  clap(): void {
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
