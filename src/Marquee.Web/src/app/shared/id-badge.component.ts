import { Component, computed, input } from '@angular/core';
import { DatePipe } from '@angular/common';
import { initialsOf } from '../core/avatar';
import { FullProfileDto, ProfileDto, isFullProfile } from '../core/models';
import { AccessCategory, accessCategoryFor } from '../core/access-category';

/**
 * Cosmetic badge serial ("No. {serial}") derived from the account id — not a real registration
 * sequence (there is no such column; deriving it avoids one for a value nobody reads meaning
 * into). Deterministic and stable per account, six digits, purely decorative.
 */
function serialFor(id: string): string {
  const hex = id.replace(/-/g, '').slice(0, 8);
  const n = parseInt(hex, 16) % 1_000_000;
  return n.toString().padStart(6, '0');
}

/**
 * A per-user barcode pattern that encodes nothing — its only job is to not be identical from one
 * badge to the next. Seeded off the username so it's stable across reloads without needing a
 * dedicated field.
 */
function barcodePatternFor(seed: string): string {
  let hash = 0;
  for (let i = 0; i < seed.length; i++) hash = (hash * 31 + seed.charCodeAt(i)) | 0;
  hash = Math.abs(hash);

  const widths = [2, 1, 3, 1, 2, 1, 3, 2].map((base, i) => base + ((hash >> (i * 3)) % 2));
  const stops: string[] = [];
  let pos = 0;
  widths.forEach((width, i) => {
    const ink = i % 2 === 0;
    const color = ink ? '#1b1a17' : 'transparent';
    stops.push(`${color} ${pos}px`, `${color} ${pos + width}px`);
    pos += width;
  });
  return `repeating-linear-gradient(90deg, ${stops.join(', ')})`;
}

/**
 * The profile badge (issue #59, design handoff section 8) — a festival ID on a lanyard, the
 * identity half of the profile screen. Purely presentational and fully data-driven: everything it
 * needs is already on ProfileDto, so the only input is the payload itself.
 *
 * "Issued" (full payload: self, friend, or a public account) prints the whole card — portrait,
 * handle, access category, member-since, serial, barcode. "Unissued" (limited payload: a private
 * stranger) is the blank-fields treatment from section 5D — name only, nothing invented in its
 * place, same overall height so the layout does not jump between the two.
 */
@Component({
  selector: 'app-id-badge',
  standalone: true,
  imports: [DatePipe],
  templateUrl: './id-badge.component.html',
  styleUrl: './id-badge.component.css',
})
export class IdBadgeComponent {
  readonly profile = input.required<ProfileDto>();

  protected readonly passYear = new Date().getFullYear();

  protected readonly full = computed<FullProfileDto | null>(() => {
    const p = this.profile();
    return isFullProfile(p) ? p : null;
  });

  protected readonly issued = computed(() => this.full() !== null);

  protected readonly initials = computed(() => initialsOf(this.profile().username));

  protected readonly category = computed<AccessCategory | null>(() => {
    const f = this.full();
    return f ? accessCategoryFor(f.premieresAttended) : null;
  });

  protected readonly serial = computed(() => {
    const f = this.full();
    return f ? serialFor(f.id) : '';
  });

  protected readonly barcode = computed(() => barcodePatternFor(this.profile().username));
}
