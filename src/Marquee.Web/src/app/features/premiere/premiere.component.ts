import { Component, OnDestroy, OnInit, computed, inject, signal } from '@angular/core';
import { DecimalPipe } from '@angular/common';
import { RouterLink } from '@angular/router';
import { AuthService } from '../../core/auth.service';
import { PremiereService } from '../../core/premiere.service';
import { ClapResponse, PremiereDto } from '../../core/models';
import { environment } from '../../../environments/environment';

@Component({
  selector: 'app-premiere',
  imports: [RouterLink, DecimalPipe],
  templateUrl: './premiere.component.html',
  styleUrl: './premiere.component.css',
})
export class PremiereComponent implements OnInit, OnDestroy {
  protected readonly auth = inject(AuthService);
  private readonly premieres = inject(PremiereService);

  protected readonly premiere = signal<PremiereDto | null>(null);
  protected readonly loading = signal(true);
  protected readonly clapping = signal(false);
  protected readonly creating = signal(false);
  protected readonly error = signal<string | null>(null);

  // Single source of truth for the marquee: progress = claps / threshold (CLAUDE.md iteration 3
  // requires curtain + bulbs derived from one value; the progress bar here anticipates it).
  protected readonly progress = computed(() => {
    const p = this.premiere();
    if (!p || p.threshold <= 0) return 0;
    return Math.min(1, p.totalClaps / p.threshold);
  });

  protected readonly progressPct = computed(() => Math.round(this.progress() * 100));

  protected readonly isOpen = computed(() => {
    const s = this.premiere()?.status;
    return s === 'Opened' || s === 'AutoOpened';
  });

  private pollHandle: ReturnType<typeof setInterval> | null = null;

  ngOnInit(): void {
    this.refresh(true);
    this.pollHandle = setInterval(() => {
      // Stop refreshing once it opens — the reveal is terminal.
      if (!this.isOpen()) this.refresh(false);
    }, environment.pollIntervalMs);
  }

  ngOnDestroy(): void {
    if (this.pollHandle) clearInterval(this.pollHandle);
  }

  private refresh(initial: boolean): void {
    this.premieres.getActive().subscribe({
      next: (p) => {
        this.premiere.set(p);
        this.loading.set(false);
      },
      error: (err: unknown) => {
        this.loading.set(false);
        const status = (err as { status?: number }).status;
        if (status === 404) {
          // No active Premiere right now. Keep any already-revealed one on screen.
          if (initial) this.premiere.set(null);
        } else {
          this.error.set('Could not load the Premiere.');
        }
      },
    });
  }

  clap(): void {
    const p = this.premiere();
    if (!p || this.clapping() || this.isOpen()) return;
    this.clapping.set(true);

    this.premieres.clap(p.id).subscribe({
      next: (r: ClapResponse) => {
        this.clapping.set(false);
        this.premiere.update((cur) =>
          cur
            ? {
                ...cur,
                totalClaps: r.totalClaps,
                myClaps: r.myClaps,
                status: r.status as PremiereDto['status'],
                movie: r.movie ?? cur.movie,
              }
            : cur,
        );
      },
      error: () => {
        this.clapping.set(false);
        this.error.set('That clap did not register. Try again.');
      },
    });
  }

  createPremiere(): void {
    this.creating.set(true);
    this.error.set(null);
    this.premieres.create().subscribe({
      next: (p) => {
        this.creating.set(false);
        this.premiere.set(p);
      },
      error: (err: unknown) => {
        this.creating.set(false);
        const e = err as { error?: { error?: string } };
        this.error.set(e?.error?.error ?? 'Could not create a Premiere.');
      },
    });
  }

  protected capReached = computed(() => {
    const p = this.premiere();
    return !!p && p.myClaps >= p.registeredClapCap;
  });
}
