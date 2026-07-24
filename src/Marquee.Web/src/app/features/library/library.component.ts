import { Component, OnInit, inject, signal } from '@angular/core';
import { DecimalPipe } from '@angular/common';
import { LibraryService } from '../../core/library.service';
import { LibraryEntryDto } from '../../core/models';

@Component({
  selector: 'app-library',
  imports: [DecimalPipe],
  templateUrl: './library.component.html',
  styleUrl: './library.component.css',
})
export class LibraryComponent implements OnInit {
  private readonly library = inject(LibraryService);

  protected readonly entries = signal<LibraryEntryDto[]>([]);
  protected readonly loading = signal(true);
  protected readonly error = signal<string | null>(null);

  ngOnInit(): void {
    this.library.mine().subscribe({
      next: (e) => {
        this.entries.set(e);
        this.loading.set(false);
      },
      error: () => {
        this.loading.set(false);
        this.error.set('Could not load your library.');
      },
    });
  }

  emblemLabel(tier: number | null): string {
    return tier ? `Emblem tier ${tier}` : 'No emblem';
  }
}
