import { ComponentFixture, TestBed } from '@angular/core/testing';
import { AvatarCropDialogComponent } from './avatar-crop-dialog.component';

describe('AvatarCropDialogComponent', () => {
  let fixture: ComponentFixture<AvatarCropDialogComponent>;
  let el: HTMLElement;

  function open(file: File) {
    TestBed.configureTestingModule({ imports: [AvatarCropDialogComponent] });
    fixture = TestBed.createComponent(AvatarCropDialogComponent);
    fixture.componentRef.setInput('file', file);
    fixture.detectChanges();
    el = fixture.nativeElement as HTMLElement;
  }

  /** Loading a photo is asynchronous (createImageBitmap); wait for the dialog to settle. */
  async function until(condition: () => boolean): Promise<void> {
    for (let i = 0; i < 100 && !condition(); i++) {
      await new Promise((r) => setTimeout(r, 20));
      fixture.detectChanges();
    }
    expect(condition()).withContext('timed out waiting for the dialog').toBeTrue();
  }

  const saveButton = () =>
    [...el.querySelectorAll('button')].find((b) => b.textContent?.trim() === 'Save photo') as HTMLButtonElement;
  const cancelButton = () =>
    [...el.querySelectorAll('button')].find((b) => b.textContent?.trim() === 'Cancel') as HTMLButtonElement;
  const errorText = () => el.querySelector('.crop__error')?.textContent?.trim() ?? null;

  it('shows the frame with its round guide, the hint and both actions', async () => {
    open(await pngFile(400, 300, 'teal'));
    await until(() => !saveButton().disabled);

    expect(el.querySelector('h2')?.textContent?.trim()).toBe('Place your photo');
    expect(el.textContent).toContain('Drag to move. The dashed circle is what small round versions show.');
    expect(el.querySelector('.crop__guide')).not.toBeNull();
    expect(cancelButton()).toBeTruthy();
  });

  it('says so, and offers nothing to save, when the browser cannot open the file', async () => {
    open(new File(['not a picture'], 'IMG_0001.HEIC', { type: 'image/heic' }));
    await until(() => errorText() !== null);

    expect(errorText()).toBe("That file type isn't supported. Choose a JPEG, PNG or WebP.");
    expect(saveButton().disabled).toBeTrue();
  });

  it('accepts a large original, since only the crop is uploaded', async () => {
    // A real PNG followed by 12 MB of padding: still decodes, and is far over the 5 MB upload limit.
    const png = await pngFile(400, 300, 'teal');
    open(new File([png, new Uint8Array(12_000_000)], 'big.png', { type: 'image/png' }));
    await until(() => !saveButton().disabled);

    expect(errorText()).toBeNull();
  });

  it('saves a 640×640 square', async () => {
    open(await pngFile(1200, 800, 'teal'));
    await until(() => !saveButton().disabled);
    const saved = new Promise<Blob>((resolve) => fixture.componentInstance.saved.subscribe(resolve));

    saveButton().click();
    const bitmap = await createImageBitmap(await saved);

    expect([bitmap.width, bitmap.height]).toEqual([640, 640]);
  });

  it('turns a sideways phone photo upright from its EXIF orientation', async () => {
    // Stored 400×200, left half red, right half blue, tagged orientation 6 (rotate 90° clockwise):
    // upright, red is on top.
    open(await sidewaysJpeg());
    await until(() => !saveButton().disabled);
    const saved = new Promise<Blob>((resolve) => fixture.componentInstance.saved.subscribe(resolve));

    saveButton().click();
    const [r, , b] = await pixel(await saved, 320, 10);

    expect(r).toBeGreaterThan(200);
    expect(b).toBeLessThan(60);
  });

  it('cancels on Escape', async () => {
    open(await pngFile(400, 300, 'teal'));
    await until(() => !saveButton().disabled);
    let cancelled = false;
    fixture.componentInstance.cancelled.subscribe(() => (cancelled = true));

    el.querySelector('.scrim')!.dispatchEvent(new KeyboardEvent('keydown', { key: 'Escape' }));

    expect(cancelled).toBeTrue();
  });

  it('disables saving while the photo is being uploaded', async () => {
    open(await pngFile(400, 300, 'teal'));
    await until(() => !saveButton().disabled);

    fixture.componentRef.setInput('busy', true);
    fixture.detectChanges();

    expect(saveButton().disabled).toBeTrue();
  });
});

function canvasOf(width: number, height: number): [HTMLCanvasElement, CanvasRenderingContext2D] {
  const canvas = document.createElement('canvas');
  canvas.width = width;
  canvas.height = height;
  return [canvas, canvas.getContext('2d')!];
}

function toBlob(canvas: HTMLCanvasElement, type: string): Promise<Blob> {
  return new Promise((resolve) => canvas.toBlob((b) => resolve(b!), type));
}

async function pngFile(width: number, height: number, color: string): Promise<File> {
  const [canvas, ctx] = canvasOf(width, height);
  ctx.fillStyle = color;
  ctx.fillRect(0, 0, width, height);
  return new File([await toBlob(canvas, 'image/png')], 'photo.png', { type: 'image/png' });
}

/** A JPEG with an EXIF segment (APP1) carrying only orientation 6, inserted after its start marker. */
async function sidewaysJpeg(): Promise<File> {
  const [canvas, ctx] = canvasOf(400, 200);
  ctx.fillStyle = 'rgb(255,0,0)';
  ctx.fillRect(0, 0, 200, 200);
  ctx.fillStyle = 'rgb(0,0,255)';
  ctx.fillRect(200, 0, 200, 200);
  const jpeg = new Uint8Array(await (await toBlob(canvas, 'image/jpeg')).arrayBuffer());

  const tiff = [0x4d, 0x4d, 0x00, 0x2a, 0, 0, 0, 8, 0, 1, 0x01, 0x12, 0, 3, 0, 0, 0, 1, 0, 6, 0, 0, 0, 0, 0, 0];
  const payload = [0x45, 0x78, 0x69, 0x66, 0, 0, ...tiff]; // "Exif\0\0" + TIFF
  const length = payload.length + 2;
  const app1 = [0xff, 0xe1, length >> 8, length & 0xff, ...payload];

  return new File([jpeg.slice(0, 2), new Uint8Array(app1), jpeg.slice(2)], 'sideways.jpg', { type: 'image/jpeg' });
}

async function pixel(blob: Blob, x: number, y: number): Promise<Uint8ClampedArray> {
  const bitmap = await createImageBitmap(blob);
  const [, ctx] = canvasOf(bitmap.width, bitmap.height);
  ctx.drawImage(bitmap, 0, 0);
  return ctx.getImageData(x, y, 1, 1).data;
}
