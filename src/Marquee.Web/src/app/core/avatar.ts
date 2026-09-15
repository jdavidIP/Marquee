/** ~14 dark tones a monogram avatar is assigned from when no picture exists (design handoff). */
const MONOGRAM_PALETTE = [
  '#3a2c4e',
  '#2c3f50',
  '#4e2c34',
  '#2c3550',
  '#2f4444',
  '#453250',
  '#2c4a44',
  '#503a2c',
  '#333f57',
  '#4a2c3d',
  '#2c4257',
  '#3d4a2c',
  '#503045',
];

/**
 * The two letters that stand in for a face. Usernames are a single token, so this is the first
 * two characters rather than initials of separate words — "yourname" reads as YO.
 */
export function initialsOf(username: string): string {
  return username.slice(0, 2).toUpperCase();
}

/** Deterministic per-user monogram background — stable across renders, reloads and other viewers. */
export function monogramColor(seed: string): string {
  let hash = 0;
  for (let i = 0; i < seed.length; i++) hash = (hash * 31 + seed.charCodeAt(i)) | 0;
  return MONOGRAM_PALETTE[Math.abs(hash) % MONOGRAM_PALETTE.length];
}
