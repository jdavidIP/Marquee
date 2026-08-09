import { Component, computed, effect, inject, input, signal, untracked } from '@angular/core';
import { CommonModule } from '@angular/common';
import { FormsModule } from '@angular/forms';
import { Router } from '@angular/router';
import { AdminService } from '../../core/admin.service';
import { apiError } from '../../core/http-error';
import { AdminPremiereDto, PremiereEditOptionsDto, PremiereStatus } from '../../core/models';
import { ConfirmDialogComponent } from '../../shared/confirm-dialog.component';
import { AdminMoviePickerComponent } from './admin-movie-picker.component';

const PAGE_SIZE = 25;

/** Which inline editor a card currently has open. */
type Editor = 'schedule' | 'threshold' | 'movie';

/** The filter options, in lifecycle order. Sentence case per CLAUDE.md §7. */
const STATUS_FILTERS: ReadonlyArray<{ value: PremiereStatus | ''; label: string }> = [
  { value: '', label: 'All' },
  { value: 'Scheduled', label: 'Scheduled' },
  { value: 'Active', label: 'Active' },
  { value: 'Opened', label: 'Opened' },
  { value: 'AutoOpened', label: 'Auto-opened' },
  { value: 'Missed', label: 'Missed' },
];

/**
 * How far a start can drift from its scheduled time before the card says so. The scheduler allows a
 * grace period for a brief restart, so a small gap is expected and not worth remarking on.
 */
const LATE_START_THRESHOLD_MINUTES = 5;

@Component({
  selector: 'app-admin-premieres',
  standalone: true,
  imports: [CommonModule, FormsModule, ConfirmDialogComponent, AdminMoviePickerComponent],
  templateUrl: './admin-premieres.component.html',
  styleUrls: ['./admin-tables.css', './admin-premieres.component.css'],
})
export class AdminPremieresComponent {
  private readonly admin = inject(AdminService);
  private readonly router = inject(Router);

  /** Bound from the query string, so a refresh or the back button lands on the same view. */
  readonly status = input<string>('');
  readonly page = input<string>('1');

  protected readonly premieres = signal<AdminPremiereDto[]>([]);
  protected readonly total = signal(0);
  protected readonly loading = signal(true);
  protected readonly refreshing = signal(false);
  protected readonly error = signal<string | null>(null);

  protected readonly filters = STATUS_FILTERS;
  protected readonly pageSize = PAGE_SIZE;

  // ------------------------------------------------------------------ editor state

  /** The card with an editor open, and which one. Only ever one at a time. */
  protected readonly openCardId = signal<string | null>(null);
  protected readonly openEditor = signal<Editor | null>(null);

  /**
   * What the server says this Premiere may be changed to. Fetched when an editor opens rather than
   * for every card: the allowed windows depend on the day's other Premieres, so it is a real query,
   * and 25 per page would be 25 requests for constraints nobody asked to see.
   *
   * Note what is not here — the frontend never recomputes §4.4 or §4.2. It renders the bounds the
   * server derived and lets the server reject anything outside them, because a second copy of those
   * formulas in TypeScript would drift from Marquee.Domain and start quietly lying.
   */
  protected readonly options = signal<PremiereEditOptionsDto | null>(null);
  protected readonly optionsLoading = signal(false);

  protected scheduleInput = '';
  protected thresholdInput: number | null = null;

  protected readonly saving = signal(false);

  /** Anchored to a card rather than the page, so a failure names the row it belongs to. */
  protected readonly actionError = signal<{ id: string; message: string } | null>(null);

  protected readonly pendingActivate = signal<AdminPremiereDto | null>(null);

  /**
   * withComponentInputBinding can hand an input undefined when the URL omits the param entirely,
   * overriding the declared default rather than falling back to it — so these are guarded rather
   * than trusted.
   */
  protected readonly activeFilter = computed<PremiereStatus | ''>(
    () => (this.status() ?? '') as PremiereStatus | '',
  );

  protected readonly currentPage = computed(() => {
    const parsed = Number.parseInt(this.page() ?? '1', 10);
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

  constructor() {
    // Same shape as the users screen: one code path loads data, driven by the query params, so
    // filtering, paging, refreshing and the back button all arrive the same way.
    //
    // untracked is load-bearing — load() reads premieres() to tell a first load from a refresh and
    // writes it with the result, so a tracked read would make this effect depend on its own output
    // and retrigger on every response.
    effect(() => {
      const status = this.activeFilter();
      const page = this.currentPage();
      untracked(() => this.load(status, page));
    });
  }

  protected selectFilter(status: PremiereStatus | ''): void {
    // null drops the parameter from the URL rather than leaving a bare `status=`, so a shared or
    // bookmarked "All" link reads as the unfiltered view it is.
    this.closeEditor();
    this.navigate({ status: status || null, page: 1 });
  }

  protected goToPage(page: number): void {
    this.closeEditor();
    this.navigate({ page });
  }

  protected refresh(): void {
    this.load(this.activeFilter(), this.currentPage());
  }

  // ------------------------------------------------------------------ editors

  protected toggleEditor(premiere: AdminPremiereDto, editor: Editor): void {
    if (this.isEditing(premiere, editor)) {
      this.closeEditor();
      return;
    }

    this.openCardId.set(premiere.id);
    this.openEditor.set(editor);
    this.actionError.set(null);
    this.options.set(null);
    this.scheduleInput = toLocalTimeValue(premiere.scheduledFor);
    this.thresholdInput = premiere.threshold;

    // The movie picker needs none of this — its constraints are per-film and come back with the
    // search results — so it does not pay for a request it will not read.
    if (editor === 'movie') return;

    this.optionsLoading.set(true);
    this.admin.editOptions(premiere.id).subscribe({
      next: (o) => {
        this.options.set(o);
        this.optionsLoading.set(false);
      },
      error: (err: unknown) => {
        this.optionsLoading.set(false);
        this.fail(premiere.id, err, 'Could not load what this Premiere allows.');
      },
    });
  }

  protected closeEditor(): void {
    this.openCardId.set(null);
    this.openEditor.set(null);
    this.options.set(null);
  }

  protected isEditing(premiere: AdminPremiereDto, editor: Editor): boolean {
    return this.openCardId() === premiere.id && this.openEditor() === editor;
  }

  protected saveSchedule(premiere: AdminPremiereDto): void {
    const localDate = this.options()?.localDate;
    if (!this.scheduleInput || !localDate) return;

    // Only a time is editable, so the date comes from the server's own answer rather than anything
    // the admin could have typed. The two are combined as local wall-clock and converted once —
    // the single place a timezone mistake could hide.
    const utc = toUtcIso(localDate, this.scheduleInput);
    if (!utc) return;

    this.saving.set(true);
    this.admin.reschedule(premiere.id, utc).subscribe({
      next: () => this.afterSave(),
      error: (err: unknown) => this.fail(premiere.id, err, 'Could not move this Premiere.'),
    });
  }

  protected saveThreshold(premiere: AdminPremiereDto): void {
    if (this.thresholdInput === null) return;

    this.saving.set(true);
    this.admin.setThreshold(premiere.id, this.thresholdInput).subscribe({
      next: () => this.afterSave(),
      error: (err: unknown) => this.fail(premiere.id, err, 'Could not change the threshold.'),
    });
  }

  // ------------------------------------------------------------------ activate

  protected askToActivate(premiere: AdminPremiereDto): void {
    this.pendingActivate.set(premiere);
  }

  protected confirmActivate(): void {
    const premiere = this.pendingActivate();
    if (!premiere) return;

    this.saving.set(true);
    this.admin.activate(premiere.id).subscribe({
      next: () => {
        this.pendingActivate.set(null);
        this.afterSave();
      },
      error: (err: unknown) => {
        this.pendingActivate.set(null);
        this.fail(premiere.id, err, 'Could not start this Premiere.');
      },
    });
  }

  // ------------------------------------------------------------------ presentation

  /** Pill tone per status. Active is the one that is live right now, so it gets the accent. */
  protected pillClass(status: PremiereStatus): string {
    switch (status) {
      case 'Active':
        return 'pill--warn';
      case 'Opened':
      case 'AutoOpened':
        return 'pill--ok';
      // Missed is a Premiere nobody got: worth flagging as a fault, not filed away as "finished".
      case 'Missed':
        return 'pill--bad';
      default:
        return 'pill--idle';
    }
  }

  protected statusLabel(status: PremiereStatus): string {
    return status === 'AutoOpened' ? 'Auto-opened' : status;
  }

  /** Everything on this screen is editable only while a Premiere is still Scheduled. */
  protected canEdit(p: AdminPremiereDto): boolean {
    return p.status === 'Scheduled';
  }

  /**
   * Whether this Premiere belongs to today. Activation is same-day only: starting one drawn for
   * another day would give today an extra Premiere and leave that day short, while the generator —
   * which counts by ScheduledFor — would consider both days full.
   *
   * A date comparison, not a rule reimplementation: the window and gap checks still belong to the
   * server, and a refusal there arrives with its own message. This only spares the obvious case.
   */
  protected isToday(p: AdminPremiereDto): boolean {
    const scheduled = new Date(p.scheduledFor);
    const now = new Date();
    return (
      scheduled.getFullYear() === now.getFullYear() &&
      scheduled.getMonth() === now.getMonth() &&
      scheduled.getDate() === now.getDate()
    );
  }

  protected canActivate(p: AdminPremiereDto): boolean {
    return this.canEdit(p) && this.isToday(p);
  }

  /**
   * Scheduled only, matching the server.
   *
   * It is tempting to allow this while a Premiere is running, since nobody has seen the film yet —
   * but a running Premiere can cross its threshold at any moment, and the open path reads its
   * MovieId from a Redis snapshot taken before the swap could land. The record would end up naming
   * a film nobody actually received.
   */
  protected canChangeMovie(p: AdminPremiereDto): boolean {
    return p.status === 'Scheduled';
  }

  protected movieDisabledReason(p: AdminPremiereDto): string {
    switch (p.status) {
      case 'Scheduled':
        return '';
      case 'Active':
        return 'This Premiere is running — its film is fixed now that people can clap for it.';
      case 'Missed':
        return 'This Premiere never ran, so its film was never used.';
      default:
        return 'The film is already in people’s libraries.';
    }
  }

  protected activateDisabledReason(p: AdminPremiereDto): string {
    if (!this.canEdit(p)) return this.disabledReason(p);
    if (!this.isToday(p)) return 'Only today’s Premieres can be started early — each day holds its own four.';
    return '';
  }

  /**
   * Why a control is disabled, phrased as the domain reason rather than "not allowed", and shown as
   * a title on the greyed button. For an operations tool a control that explains itself teaches the
   * state machine; one that vanishes just looks like a missing feature.
   */
  protected disabledReason(p: AdminPremiereDto): string {
    switch (p.status) {
      case 'Active':
        return 'This Premiere is already running.';
      case 'Opened':
      case 'AutoOpened':
        return 'This Premiere has already opened.';
      case 'Missed':
        return 'This Premiere was never run and cannot be revived.';
      default:
        return '';
    }
  }

  protected errorFor(p: AdminPremiereDto): string | null {
    const current = this.actionError();
    return current?.id === p.id ? current.message : null;
  }

  /** "07:00–09:30 or 14:15–23:00" — the times this Premiere may move to. */
  protected windowSummary(): string | null {
    const windows = this.options()?.allowedWindows ?? [];
    if (windows.length === 0) return null;
    return windows.map((w) => (w.start === w.end ? w.start : `${w.start}–${w.end}`)).join(' or ');
  }

  /**
   * Outer bounds for the time picker, taken from the first and last allowed window.
   *
   * Only an outer bound — the gaps between windows are still reachable, and the server is what
   * actually rejects them. These come from the server's own answer rather than from §4.4 being
   * re-derived here, and they exist only to stop the most obvious out-of-range pick.
   */
  protected timeMin(): string {
    return this.options()?.allowedWindows[0]?.start ?? '';
  }

  protected timeMax(): string {
    const windows = this.options()?.allowedWindows ?? [];
    return windows.length > 0 ? windows[windows.length - 1].end : '';
  }

  /**
   * How late a Premiere actually started, in minutes, or null when it started on time or never
   * started at all.
   *
   * Surfaced because the list is ordered by ScheduledFor: a Premiere that activated well after its
   * slot sits in the list under the day it was drawn for, not the day it ran, and without this the
   * discrepancy is invisible. It also makes historic rows from before the grace period existed
   * explain themselves rather than looking like corrupt data.
   */
  protected lateStartMinutes(p: AdminPremiereDto): number | null {
    if (!p.opensAt) return null;

    const drift = (new Date(p.opensAt).getTime() - new Date(p.scheduledFor).getTime()) / 60_000;
    return drift >= LATE_START_THRESHOLD_MINUTES ? Math.round(drift) : null;
  }

  /** Days is the only sensible unit once a start is more than a day adrift. */
  protected lateStartLabel(p: AdminPremiereDto): string {
    const minutes = this.lateStartMinutes(p) ?? 0;
    if (minutes < 60) return `${minutes} min late`;
    if (minutes < 60 * 24) return `${Math.round(minutes / 60)} h late`;
    return `${Math.round(minutes / (60 * 24))} days late`;
  }

  /**
   * Whether the durable clap total is meaningful yet. TotalClaps is only written when a Premiere
   * opens — live counting happens in Redis — so showing a flat 0 next to a running Premiere would
   * read as "nobody clapped" rather than "not counted here yet".
   */
  protected hasDurableCount(p: AdminPremiereDto): boolean {
    return p.status === 'Opened' || p.status === 'AutoOpened';
  }

  protected progressPct(p: AdminPremiereDto): number {
    if (p.threshold <= 0) return 0;
    return Math.min(100, Math.round((p.totalClaps / p.threshold) * 100));
  }

  /** The picker reports only that the film changed; refetching is this screen's job. */
  protected onMovieChanged(): void {
    this.afterSave();
  }

  private afterSave(): void {
    this.saving.set(false);
    this.closeEditor();
    // Refetch rather than patch from the response: AdminService returns the DTO with contributors
    // hardcoded to 0 and TotalClaps read from a column not written until open, so patching would
    // blank figures the list query computed correctly. It also picks up the status transition.
    this.load(this.activeFilter(), this.currentPage());
  }

  private fail(premiereId: string, err: unknown, fallback: string): void {
    this.saving.set(false);
    this.actionError.set({ id: premiereId, message: apiError(err, fallback) });

    // A conflict means this view is stale, so re-read: the row's status corrects itself and the
    // control that could not apply goes disabled with its reason. That closes the loop rather than
    // leaving an admin looking at a button the server has already refused.
    if ((err as { status?: number })?.status === 409) {
      this.closeEditor();
      this.load(this.activeFilter(), this.currentPage());
    }
  }

  private navigate(queryParams: Record<string, string | number | null>): void {
    this.router.navigate([], { queryParams, queryParamsHandling: 'merge', replaceUrl: true });
  }

  private load(status: PremiereStatus | '', page: number): void {
    if (this.premieres().length === 0) this.loading.set(true);
    else this.refreshing.set(true);

    // An empty filter must become null, not an empty string: the API binds status to a nullable
    // enum and `status=` fails that binding with a 400.
    this.admin.premieres({ status: status || null, page, pageSize: PAGE_SIZE }).subscribe({
      next: (result) => {
        this.premieres.set(result.items);
        this.total.set(result.total);
        this.error.set(null);
        this.loading.set(false);
        this.refreshing.set(false);
      },
      error: (err: unknown) => {
        // Existing cards stay on screen; one failed load should not empty the view.
        this.error.set(apiError(err, 'Could not load Premieres.'));
        this.loading.set(false);
        this.refreshing.set(false);
      },
    });
  }
}

const pad = (n: number) => String(n).padStart(2, '0');

/** UTC instant -> the local "HH:mm" a time input expects. */
function toLocalTimeValue(iso: string): string {
  const d = new Date(iso);
  return `${pad(d.getHours())}:${pad(d.getMinutes())}`;
}

/**
 * A local date ("YYYY-MM-DD") plus a local time ("HH:mm") -> the UTC instant the API stores.
 *
 * Built through the Date constructor rather than by parsing a combined string: a date-time string
 * without a zone is read as local, but a date-only one is read as UTC, and relying on which rule
 * applies to a concatenation is how an off-by-one-day bug gets in.
 */
function toUtcIso(localDate: string, localTime: string): string | null {
  const [year, month, day] = localDate.split('-').map(Number);
  const [hour, minute] = localTime.split(':').map(Number);

  if ([year, month, day, hour, minute].some((n) => !Number.isFinite(n))) return null;

  return new Date(year, month - 1, day, hour, minute, 0, 0).toISOString();
}
