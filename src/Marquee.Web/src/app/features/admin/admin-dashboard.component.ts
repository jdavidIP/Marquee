import { Component, OnDestroy, OnInit, signal } from '@angular/core';
import { CommonModule } from '@angular/common';
import { AdminService } from '../../core/admin.service';
import { AdminMetricsDto } from '../../core/models';

/**
 * Live operational view: queue depth, connected watchers, clap rate (Iteration 6).
 *
 * Polls rather than subscribing over SignalR. The hub carries Premiere state to everyone watching,
 * and pushing operator-only telemetry down it would mean either a second hub or broadcasting
 * infrastructure numbers to every connected visitor. A poll from one admin tab is the cheaper and
 * narrower option; the interval is generous because these numbers are for noticing trends, not for
 * reacting within a second.
 */
@Component({
  selector: 'app-admin-dashboard',
  standalone: true,
  imports: [CommonModule],
  templateUrl: './admin-dashboard.component.html',
  styleUrl: './admin-dashboard.component.css',
})
export class AdminDashboardComponent implements OnInit, OnDestroy {
  private static readonly PollMs = 3000;

  readonly metrics = signal<AdminMetricsDto | null>(null);
  readonly error = signal<string | null>(null);
  readonly loading = signal(true);

  private timer: ReturnType<typeof setInterval> | null = null;

  constructor(private admin: AdminService) {}

  ngOnInit(): void {
    this.refresh();
    this.timer = setInterval(() => this.refresh(), AdminDashboardComponent.PollMs);
  }

  ngOnDestroy(): void {
    if (this.timer) clearInterval(this.timer);
  }

  private refresh(): void {
    this.admin.metrics().subscribe({
      next: (m) => {
        this.metrics.set(m);
        this.error.set(null);
        this.loading.set(false);
      },
      error: () => {
        // Keep the last good reading on screen. Blanking the panel because one poll failed loses
        // the very numbers someone opened it to look at.
        this.error.set('Could not reach the metrics endpoint.');
        this.loading.set(false);
      },
    });
  }

  /** Fraction of the way to the threshold, for the progress bar. */
  progress(m: AdminMetricsDto): number {
    if (!m.activePremiereThreshold) return 0;
    return Math.min(100, Math.round((m.liveClaps / m.activePremiereThreshold) * 100));
  }
}
