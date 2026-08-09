import { Component, inject, input, output, signal } from '@angular/core';
import { CommonModule } from '@angular/common';
import { FormsModule } from '@angular/forms';
import { AdminService } from '../../core/admin.service';
import { apiError, isCooldownConflict } from '../../core/http-error';
import { CountryDto, GenreDto, MovieFilterRequest, MovieSearchResultDto } from '../../core/models';
import { ConfirmDialogComponent } from '../../shared/confirm-dialog.component';

const SEARCH_DEBOUNCE_MS = 350;

/** The two ways to change a Premiere's film, which answer different needs. */
type Mode = 'search' | 'filter';

/**
 * Changes the film a Premiere is holding: search for one and pick it, or re-roll within a narrower
 * pool.
 *
 * Its own component because it carries a lot of state of its own — a debounced search, two
 * reference lists, eight filter fields and a cooldown override — none of which the Premieres list
 * needs to know about. The list only cares that the film changed, which is the single output here.
 */
@Component({
  selector: 'app-admin-movie-picker',
  standalone: true,
  imports: [CommonModule, FormsModule, ConfirmDialogComponent],
  templateUrl: './admin-movie-picker.component.html',
  styleUrl: './admin-movie-picker.component.css',
})
export class AdminMoviePickerComponent {
  private readonly admin = inject(AdminService);

  readonly premiereId = input.required<string>();
  readonly currentTitle = input<string>('');

  /** Emitted once the Premiere's film has actually changed, so the list can refetch. */
  readonly changed = output<void>();

  protected readonly mode = signal<Mode>('search');
  protected readonly busy = signal(false);
  protected readonly error = signal<string | null>(null);

  // ------------------------------------------------------------------ search

  protected query = '';
  protected readonly results = signal<MovieSearchResultDto[]>([]);
  protected readonly searching = signal(false);
  protected readonly searched = signal(false);

  /** The film an admin picked that is still resting, held until they confirm the override (§4.6). */
  protected readonly pendingCooldown = signal<MovieSearchResultDto | null>(null);

  private searchTimer: ReturnType<typeof setTimeout> | null = null;

  // ------------------------------------------------------------------ filters

  protected readonly genres = signal<GenreDto[]>([]);
  protected readonly countries = signal<CountryDto[]>([]);

  protected filter: MovieFilterRequest = {};

  constructor() {
    // Read from the local tables rather than TMDB, so the filter controls still work when TMDB does
    // not — and so they list exactly what the films here are actually linked to.
    this.admin.genres().subscribe({ next: (g) => this.genres.set(g), error: () => this.genres.set([]) });
    this.admin.countries().subscribe({
      next: (c) => this.countries.set(c),
      error: () => this.countries.set([]),
    });
  }

  protected setMode(mode: Mode): void {
    this.mode.set(mode);
    this.error.set(null);
  }

  // ------------------------------------------------------------------ searching

  protected onQueryInput(value: string): void {
    this.query = value;
    if (this.searchTimer) clearTimeout(this.searchTimer);

    if (!value.trim()) {
      this.results.set([]);
      this.searched.set(false);
      return;
    }

    this.searchTimer = setTimeout(() => this.search(), SEARCH_DEBOUNCE_MS);
  }

  private search(): void {
    const term = this.query.trim();
    if (!term) return;

    this.searching.set(true);
    this.admin.searchMovies(term).subscribe({
      next: (hits) => {
        this.results.set(hits);
        this.searching.set(false);
        this.searched.set(true);
        this.error.set(null);
      },
      error: (err: unknown) => {
        this.searching.set(false);
        this.searched.set(true);
        this.error.set(apiError(err, 'Could not search TMDB.'));
      },
    });
  }

  // ------------------------------------------------------------------ choosing

  protected choose(hit: MovieSearchResultDto): void {
    // Asked before sending rather than after being refused: the admin already has the dates in
    // front of them, so making them collect a 409 first would be theatre.
    if (hit.inCooldown) {
      this.pendingCooldown.set(hit);
      return;
    }

    this.setMovie(hit.tmdbId, false);
  }

  protected confirmCooldownOverride(): void {
    const hit = this.pendingCooldown();
    if (!hit) return;

    this.setMovie(hit.tmdbId, true);
  }

  private setMovie(tmdbId: number, acknowledge: boolean): void {
    this.busy.set(true);
    this.admin.setMovie(this.premiereId(), tmdbId, acknowledge).subscribe({
      next: () => {
        this.busy.set(false);
        this.pendingCooldown.set(null);
        this.changed.emit();
      },
      error: (err: unknown) => {
        this.busy.set(false);
        this.pendingCooldown.set(null);

        // A cooldown refusal is recoverable — the same request with an acknowledgement succeeds —
        // so it is worth saying so rather than reporting a flat failure.
        this.error.set(
          isCooldownConflict(err)
            ? `${apiError(err, 'That film is still resting.')} Search for it again to confirm.`
            : apiError(err, 'Could not set that film.'),
        );
      },
    });
  }

  // ------------------------------------------------------------------ re-rolling

  protected reroll(): void {
    this.busy.set(true);
    this.admin.regenerateMovie(this.premiereId(), this.cleanFilter()).subscribe({
      next: () => {
        this.busy.set(false);
        this.changed.emit();
      },
      error: (err: unknown) => {
        this.busy.set(false);
        this.error.set(
          apiError(err, 'Could not find another film. Try widening the filter.'),
        );
      },
    });
  }

  /**
   * Drops empty fields so an untouched filter is sent as {} rather than a wall of nulls, and the
   * API sees "no narrowing" instead of a filter that happens to constrain nothing.
   */
  private cleanFilter(): MovieFilterRequest {
    const entries = Object.entries(this.filter).filter(([, v]) => v !== null && v !== undefined && v !== '');
    return Object.fromEntries(entries) as MovieFilterRequest;
  }

  protected clearFilter(): void {
    this.filter = {};
  }

  // ------------------------------------------------------------------ presentation

  /** Why a search hit cannot be picked, or null when it can. */
  protected blockedReason(hit: MovieSearchResultDto): string | null {
    return hit.alreadyQueued ? 'Already lined up for another Premiere' : null;
  }
}
