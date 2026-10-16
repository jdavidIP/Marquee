/**
 * A cosmetic six-digit serial ("No. 000123") derived from an entity's id — not a stored sequence.
 * Deterministic and stable across reloads without needing a dedicated column. Used by the profile
 * badge (issue #59) and the Premiere ticket stub (issue #58).
 */
export function serialFor(id: string): string {
  const hex = id.replace(/-/g, '').slice(0, 8);
  const n = parseInt(hex, 16) % 1_000_000;
  return n.toString().padStart(6, '0');
}
