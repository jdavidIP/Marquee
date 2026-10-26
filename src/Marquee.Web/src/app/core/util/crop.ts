/**
 * The arithmetic behind the profile photo crop (#63), kept out of the dialog so it can be tested
 * without a canvas.
 *
 * The photo sits behind a square frame `frame` CSS px wide. At zoom 1 its short side exactly fills
 * the frame; zoom multiplies that. `offset` is how far the user has dragged it from centred, in frame
 * px — positive x moves the photo right, which shows more of its left side.
 */
export interface Size {
  width: number;
  height: number;
}

export interface Offset {
  x: number;
  y: number;
}

/** The part of the photo, in its own pixels, that ends up inside the frame. */
export interface SourceRect {
  sx: number;
  sy: number;
  sw: number;
  sh: number;
}

/** How many on-screen px one source px takes up. */
export function displayScale(natural: Size, frame: number, zoom: number): number {
  return (frame * zoom) / Math.min(natural.width, natural.height);
}

/** The offset pulled back so the frame never shows anything outside the photo. */
export function clampOffset(natural: Size, frame: number, zoom: number, offset: Offset): Offset {
  const scale = displayScale(natural, frame, zoom);
  const maxX = (natural.width * scale - frame) / 2;
  const maxY = (natural.height * scale - frame) / 2;
  return { x: clamp(offset.x, maxX), y: clamp(offset.y, maxY) };
}

export function cropSource(natural: Size, frame: number, zoom: number, offset: Offset): SourceRect {
  const scale = displayScale(natural, frame, zoom);
  const { x, y } = clampOffset(natural, frame, zoom, offset);
  const side = frame / scale;
  return {
    sx: (natural.width - side) / 2 - x / scale,
    sy: (natural.height - side) / 2 - y / scale,
    sw: side,
    sh: side,
  };
}

function clamp(value: number, limit: number): number {
  // + 0 turns a -0 (from clamping to a zero limit) into 0, which matters only to strict equality.
  return Math.max(-limit, Math.min(limit, value)) + 0;
}
