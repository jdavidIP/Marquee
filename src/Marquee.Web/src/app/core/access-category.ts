/**
 * The profile badge's "access category" ladder (design handoff, issue #59) — earned by Premieres
 * attended, not movies collected, so a night the threshold was missed still counts. It is a record,
 * not a permission: nothing in the app is gated on it. Deliberately separate from the library's
 * emblem-tier materials (paper→platinum), which measure one Premiere's clap share instead of
 * lifetime attendance — the two must not be unified.
 */
export interface AccessCategory {
  readonly step: number;
  readonly name: string;
  readonly ink: string;
  readonly tint: string;
  readonly min: number;
  /** Null means no ceiling — Jury, the top of the ladder. */
  readonly max: number | null;
}

/** Boundaries are inclusive-inclusive, in ascending order. */
export const ACCESS_CATEGORIES: readonly AccessCategory[] = [
  { step: 1, name: 'Standby', ink: '#6b7280', tint: '#e2e5ea', min: 0, max: 4 },
  { step: 2, name: 'General', ink: '#2d5a6b', tint: '#cfe0e6', min: 5, max: 14 },
  { step: 3, name: 'Industry', ink: '#34407a', tint: '#c5cdea', min: 15, max: 39 },
  { step: 4, name: 'Press', ink: '#a83c2c', tint: '#f3cec6', min: 40, max: 99 },
  { step: 5, name: 'Jury', ink: '#1b1e26', tint: '#c6a35a', min: 100, max: null },
];

/** The single place this is computed — never re-derive the ladder inline per view. */
export function accessCategoryFor(premieresAttended: number): AccessCategory {
  return (
    ACCESS_CATEGORIES.find(
      (c) => premieresAttended >= c.min && (c.max === null || premieresAttended <= c.max),
    ) ?? ACCESS_CATEGORIES[ACCESS_CATEGORIES.length - 1]
  );
}

/** Null at Jury — there is no next category, so the self-only progress block hides entirely. */
export function nextAccessCategory(current: AccessCategory): AccessCategory | null {
  return ACCESS_CATEGORIES[current.step] ?? null;
}
