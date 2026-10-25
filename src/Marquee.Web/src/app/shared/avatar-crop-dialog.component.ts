import { Component, DestroyRef, ElementRef, effect, inject, input, output, signal, viewChild } from '@angular/core';
import { Offset, clampOffset, cropSource, displayScale } from '../core/util/crop';

/** CSS px. The stage is the visible area; the frame inside it is the square that gets saved. */
const STAGE = 300;
const FRAME = 240;
/** Side of the exported square; the API re-encodes to the same size (#63). */
const EXPORT = 640;
/** How far one arrow-key press moves the photo, in frame px. */
const NUDGE = 8;

/**
 * Choosing which part of a photo becomes your profile picture (#63): drag it behind a square frame,
 * zoom with the slider, save. The square is the badge photo; the dashed circle inside it shows what
 * the small round faces will keep.
 *
 * Only the 640px crop leaves the browser, so a large original never meets the upload limit. The API
 * re-encodes whatever arrives regardless — this crop is a convenience, not a safeguard.
 *
 * Presentational, like ConfirmDialogComponent: the screen that opens it owns the upload.
 */
@Component({
  selector: 'app-avatar-crop-dialog',
  standalone: true,
  templateUrl: './avatar-crop-dialog.component.html',
  styleUrl: './avatar-crop-dialog.component.css',
})
export class AvatarCropDialogComponent {
  readonly file = input.required<File>();
  /** Set while the screen uploads the saved crop, so it cannot be sent twice. */
  readonly busy = input(false);

  readonly saved = output<Blob>();
  readonly cancelled = output<void>();

  protected readonly maxZoom = 3;
  protected readonly bitmap = signal<ImageBitmap | null>(null);
  protected readonly error = signal<string | null>(null);
  protected readonly zoom = signal(1);
  protected readonly offset = signal<Offset>({ x: 0, y: 0 });
  protected readonly encoding = signal(false);

  private readonly canvas = viewChild<ElementRef<HTMLCanvasElement>>('canvas');
  private readonly cancelButton = viewChild<ElementRef<HTMLButtonElement>>('cancelButton');
  private drag: { pointerId: number; x: number; y: number } | null = null;

  constructor() {
    effect(() => this.cancelButton()?.nativeElement.focus());

    // A new file starts over. The bitmap honours the EXIF orientation, so a phone photo stored
    // sideways is placed — and saved — upright.
    effect((onCleanup) => {
      const file = this.file();
      let current = true;
      onCleanup(() => (current = false));
      this.bitmap.set(null);
      this.error.set(null);
      this.zoom.set(1);
      this.offset.set({ x: 0, y: 0 });

      createImageBitmap(file, { imageOrientation: 'from-image' }).then(
        (bitmap) => (current ? this.bitmap.set(bitmap) : bitmap.close()),
        () => current && this.error.set("That file type isn't supported. Choose a JPEG, PNG or WebP."),
      );
    });

    effect(() => this.draw());

    inject(DestroyRef).onDestroy(() => this.bitmap()?.close());
  }

  protected onPointerDown(event: PointerEvent): void {
    if (!this.bitmap()) return;
    (event.currentTarget as HTMLElement).setPointerCapture(event.pointerId);
    this.drag = { pointerId: event.pointerId, x: event.clientX, y: event.clientY };
  }

  protected onPointerMove(event: PointerEvent): void {
    if (this.drag?.pointerId !== event.pointerId) return;
    this.moveBy(event.clientX - this.drag.x, event.clientY - this.drag.y);
    this.drag = { ...this.drag, x: event.clientX, y: event.clientY };
  }

  protected onPointerUp(event: PointerEvent): void {
    if (this.drag?.pointerId === event.pointerId) this.drag = null;
  }

  protected onKey(event: KeyboardEvent): void {
    const step: Record<string, [number, number]> = {
      ArrowLeft: [-NUDGE, 0],
      ArrowRight: [NUDGE, 0],
      ArrowUp: [0, -NUDGE],
      ArrowDown: [0, NUDGE],
    };
    const delta = step[event.key];
    if (!delta) return;
    event.preventDefault();
    this.moveBy(...delta);
  }

  protected onZoom(event: Event): void {
    const bitmap = this.bitmap();
    if (!bitmap) return;
    const zoom = Number((event.target as HTMLInputElement).value);
    this.zoom.set(zoom);
    // Zooming out shrinks how far the photo may sit off-centre.
    this.offset.set(clampOffset(bitmap, FRAME, zoom, this.offset()));
  }

  protected onCancel(): void {
    if (!this.busy()) this.cancelled.emit();
  }

  protected async onSave(): Promise<void> {
    const bitmap = this.bitmap();
    if (!bitmap || this.busy() || this.encoding()) return;
    this.encoding.set(true);
    try {
      const { sx, sy, sw, sh } = cropSource(bitmap, FRAME, this.zoom(), this.offset());
      const out = document.createElement('canvas');
      out.width = EXPORT;
      out.height = EXPORT;
      const ctx = out.getContext('2d')!;
      ctx.imageSmoothingQuality = 'high';
      ctx.drawImage(bitmap, sx, sy, sw, sh, 0, 0, EXPORT, EXPORT);

      // A browser that cannot encode WebP hands back a PNG instead; JPEG is far smaller than that,
      // and the API accepts either.
      let blob = await toBlob(out, 'image/webp');
      if (blob?.type !== 'image/webp') blob = await toBlob(out, 'image/jpeg');
      if (blob) this.saved.emit(blob);
    } finally {
      this.encoding.set(false);
    }
  }

  private moveBy(dx: number, dy: number): void {
    const bitmap = this.bitmap();
    if (!bitmap) return;
    const { x, y } = this.offset();
    this.offset.set(clampOffset(bitmap, FRAME, this.zoom(), { x: x + dx, y: y + dy }));
  }

  private draw(): void {
    const canvas = this.canvas()?.nativeElement;
    const bitmap = this.bitmap();
    const zoom = this.zoom();
    const { x, y } = this.offset();
    if (!canvas) return;

    const ratio = window.devicePixelRatio || 1;
    canvas.width = STAGE * ratio;
    canvas.height = STAGE * ratio;
    const ctx = canvas.getContext('2d')!;
    ctx.setTransform(ratio, 0, 0, ratio, 0, 0);
    ctx.clearRect(0, 0, STAGE, STAGE);
    if (!bitmap) return;

    const scale = displayScale(bitmap, FRAME, zoom);
    const width = bitmap.width * scale;
    const height = bitmap.height * scale;
    ctx.imageSmoothingQuality = 'high';
    ctx.drawImage(bitmap, (STAGE - width) / 2 + x, (STAGE - height) / 2 + y, width, height);
  }
}

function toBlob(canvas: HTMLCanvasElement, type: string): Promise<Blob | null> {
  return new Promise((resolve) => canvas.toBlob(resolve, type, 0.92));
}
