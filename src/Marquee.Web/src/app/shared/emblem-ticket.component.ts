import { Component, computed, input } from '@angular/core';

interface EmblemMaterial {
  name: string;
  gradient: string;
  ink: string;
  edge: string;
  dash: string;
}

/**
 * The five-material ladder (CLAUDE.md §4.3's tiers, printed rather than coloured so metal is
 * carried by the material name and edge, not by a flood of colour). Paper → Bronze → Silver →
 * Gold → Platinum, one entry per emblem tier.
 */
const TIER_MATERIALS: Record<number, EmblemMaterial> = {
  1: {
    name: 'Paper',
    gradient: 'linear-gradient(180deg, #e6dfcd, #cfc5ad)',
    ink: '#2a2620',
    edge: '#b3a98f',
    dash: 'rgba(42, 38, 32, 0.45)',
  },
  2: {
    name: 'Bronze',
    gradient: 'linear-gradient(180deg, #c8894b, #8d5a2c)',
    ink: '#1e1409',
    edge: '#6e441f',
    dash: 'rgba(28, 20, 9, 0.5)',
  },
  3: {
    name: 'Silver',
    gradient: 'linear-gradient(180deg, #dde4eb, #9aa5b3)',
    ink: '#15181d',
    edge: '#7d8794',
    dash: 'rgba(21, 24, 29, 0.45)',
  },
  4: {
    name: 'Gold',
    gradient: 'linear-gradient(180deg, #f2cf78, #c99327)',
    ink: '#241a06',
    edge: '#a2760f',
    dash: 'rgba(36, 26, 6, 0.5)',
  },
  5: {
    name: 'Platinum',
    gradient:
      'linear-gradient(125deg, #f4f8fb 0%, #cfe4e2 28%, #e9e2ee 52%, #dbe7f1 76%, #f6f9fc 100%)',
    ink: '#0f1319',
    edge: '#9fb0bd',
    dash: 'rgba(15, 19, 25, 0.4)',
  },
};

/**
 * The ticket band under a library poster — a die-cut stub rather than a coloured badge, per the
 * design handoff's "the material ladder the user chose over an access-based one."
 *
 * Presentational only, like ConfirmDialogComponent: it holds no state of its own, just a tier.
 * Two sizes are built: 'library' (46px, punch notches, the library grid) and 'compact' (30px, no
 * notches, the profile badge's recent-activity strip — issue #59). The handoff's larger "3A"
 * reference-sheet stub (with a condition subtitle, "Admit one" and a serial) is explicitly an
 * optional dev-only sanity sheet with no real screen consuming it, so there's nothing to build it
 * against yet; add that form if/when something actually needs it.
 */
@Component({
  selector: 'app-emblem-ticket',
  standalone: true,
  templateUrl: './emblem-ticket.component.html',
  styleUrl: './emblem-ticket.component.css',
})
export class EmblemTicketComponent {
  /** 1–5, per CLAUDE.md §4.3. Falls back to Paper for anything else so a bad value never renders blank. */
  readonly tier = input.required<number>();

  readonly size = input<'library' | 'compact'>('library');

  protected readonly material = computed<EmblemMaterial>(
    () => TIER_MATERIALS[this.tier()] ?? TIER_MATERIALS[1],
  );
}
