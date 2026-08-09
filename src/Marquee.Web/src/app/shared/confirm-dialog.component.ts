import { Component, ElementRef, effect, input, output, viewChild } from '@angular/core';
import { FormsModule } from '@angular/forms';

/**
 * A modal confirmation, for actions that are hard to take back.
 *
 * Not window.confirm: this has to name the film being replaced, escalate its wording when a Premiere
 * is already running, and collect a block reason. None of that fits a browser dialog, and one would
 * look foreign against the app's chrome besides.
 *
 * Presentational only — it holds no state about what is being confirmed. The screen owns that, which
 * keeps one dialog instance per screen instead of a dialog service.
 */
@Component({
  selector: 'app-confirm-dialog',
  standalone: true,
  imports: [FormsModule],
  templateUrl: './confirm-dialog.component.html',
  styleUrl: './confirm-dialog.component.css',
})
export class ConfirmDialogComponent {
  readonly title = input.required<string>();
  readonly body = input.required<string>();
  readonly confirmLabel = input('Confirm');
  readonly tone = input<'default' | 'danger'>('default');
  readonly busy = input(false);

  /** When set, renders a textarea and passes its contents out with the confirmation. */
  readonly reasonLabel = input<string | null>(null);

  readonly confirmed = output<string | null>();
  readonly cancelled = output<void>();

  protected reason = '';

  private readonly cancelButton = viewChild<ElementRef<HTMLButtonElement>>('cancelButton');

  constructor() {
    // Focus lands on Cancel, never on the destructive button: a stray Enter or Space arriving as the
    // dialog opens must not be the thing that starts a Premiere for everyone.
    effect(() => this.cancelButton()?.nativeElement.focus());
  }

  protected onConfirm(): void {
    this.confirmed.emit(this.reasonLabel() ? this.reason.trim() || null : null);
  }

  protected onCancel(): void {
    if (!this.busy()) this.cancelled.emit();
  }
}
