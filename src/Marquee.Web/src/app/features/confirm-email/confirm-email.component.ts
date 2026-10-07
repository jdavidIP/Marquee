import { Component, inject, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { ActivatedRoute, Router, RouterLink } from '@angular/router';
import { AuthService } from '../../core/auth.service';
import { authError } from '../../core/cognito';

type ConfirmStatus = 'form' | 'confirmed';

/**
 * Where an account's emailed code is entered (DEPLOYMENT.md § Phase 2, decision 3) — reached right
 * after registering, and whenever signing in finds the account unconfirmed. Both arrive with the
 * username in `?u=`; it stays editable, so the page also works opened by hand.
 *
 * Confirming signs straight in when this tab still holds the password typed moments ago; otherwise
 * it says so and sends the person to sign in.
 */
@Component({
  selector: 'app-confirm-email',
  imports: [FormsModule, RouterLink],
  templateUrl: './confirm-email.component.html',
  styleUrl: './confirm-email.component.css',
})
export class ConfirmEmailComponent {
  private readonly route = inject(ActivatedRoute);
  private readonly router = inject(Router);
  private readonly auth = inject(AuthService);

  protected readonly status = signal<ConfirmStatus>('form');
  protected readonly busy = signal(false);
  protected readonly error = signal<string | null>(null);
  /** Confirmation that a new code went out — shown in place of an error, never alongside one. */
  protected readonly notice = signal<string | null>(null);

  protected username = this.route.snapshot.queryParamMap.get('u') ?? '';
  protected code = '';

  submit(): void {
    this.error.set(null);
    this.notice.set(null);
    this.busy.set(true);

    this.auth.confirmSignUp(this.username.trim(), this.code.trim()).subscribe({
      next: (outcome) => {
        this.busy.set(false);
        if (outcome === 'signed-in') this.router.navigate(['/premiere']);
        else this.status.set('confirmed');
      },
      error: (err) => {
        this.busy.set(false);
        this.error.set(authError(err, 'Could not confirm your account. Please try again.'));
      },
    });
  }

  resend(): void {
    this.error.set(null);
    this.notice.set(null);
    this.busy.set(true);

    this.auth.resendCode(this.username.trim()).subscribe({
      next: () => {
        this.busy.set(false);
        this.notice.set('A new code is on its way. Check your email.');
      },
      error: (err) => {
        this.busy.set(false);
        this.error.set(authError(err, 'Could not send a new code. Please try again.'));
      },
    });
  }
}
