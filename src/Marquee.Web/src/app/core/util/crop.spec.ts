import { clampOffset, cropSource } from './crop';

describe('cropSource', () => {
  const landscape = { width: 2000, height: 1000 };
  const portrait = { width: 1000, height: 2000 };

  it('fills the frame with the short side at zoom 1, centred', () => {
    expect(cropSource(landscape, 200, 1, { x: 0, y: 0 })).toEqual({ sx: 500, sy: 0, sw: 1000, sh: 1000 });
  });

  it('takes half as much of the photo at zoom 2', () => {
    expect(cropSource(landscape, 200, 2, { x: 0, y: 0 })).toEqual({ sx: 750, sy: 250, sw: 500, sh: 500 });
  });

  it('moves the window opposite to the drag, in source pixels', () => {
    // Dragging the photo 50 frame-px right shows 50 × (1000 / 200) = 250 source px further left.
    expect(cropSource(landscape, 200, 1, { x: 50, y: 0 })).toEqual({ sx: 250, sy: 0, sw: 1000, sh: 1000 });
  });

  it('never lets the frame show outside the photo', () => {
    const farRight = cropSource(landscape, 200, 1, { x: 10_000, y: 10_000 });
    expect(farRight.sx).toBe(0);
    expect(farRight.sy).toBe(0);

    const farLeft = cropSource(landscape, 200, 1, { x: -10_000, y: -10_000 });
    expect(farLeft.sx + farLeft.sw).toBe(2000);
    expect(farLeft.sy + farLeft.sh).toBe(1000);
  });

  it('mirrors the landscape case for a portrait photo', () => {
    expect(cropSource(portrait, 200, 1, { x: 0, y: 0 })).toEqual({ sx: 0, sy: 500, sw: 1000, sh: 1000 });
    expect(cropSource(portrait, 200, 2, { x: 0, y: 0 })).toEqual({ sx: 250, sy: 750, sw: 500, sh: 500 });
  });
});

describe('clampOffset', () => {
  it('limits a drag to how far the photo overhangs the frame', () => {
    // At zoom 1 a 2:1 photo shown 400×200 in a 200 frame overhangs 100 px on each side, and not at all
    // vertically.
    expect(clampOffset({ width: 2000, height: 1000 }, 200, 1, { x: 500, y: 40 })).toEqual({ x: 100, y: 0 });
    expect(clampOffset({ width: 2000, height: 1000 }, 200, 1, { x: -500, y: -40 })).toEqual({ x: -100, y: 0 });
  });
});
