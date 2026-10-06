import { Component, computed, inject, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { ActivatedRoute, RouterLink } from '@angular/router';
import { AuthService } from '../../core/auth.service';
import { authError } from '../../core/cognito';
import { PasswordRulesDto } from '../../core/models';

type ResetStatus = 'form' | 'succeeded';

/**
 * Where a password is reset with the emailed code (DEPLOYMENT.md § Phase 2, decision 3) — reached from
 * "Forgot your password?" with the username or email in `?u=`, which stays editable so the page also
 * works opened by hand. Reset codes last an hour.
 */
@Component({
  selector: 'app-reset-password',
  imports: [FormsModule, RouterLink],
  templateUrl: './reset-password.component.html',
  styleUrl: './reset-password.component.css',
})
export class ResetPasswordComponent {
  private readonly route = inject(ActivatedRoute);
  private readonly auth = inject(AuthService);

  protected readonly status = signal<ResetStatus>('form');
  protected readonly busy = signal(false);
  protected readonly error = signal<string | null>(null);
  /** Confirmation that a new code went out — shown in place of an error, never alongside one. */
  protected readonly notice = signal<string | null>(null);

  /** Same "fetch once, degrade gracefully if it fails" reasoning as LoginComponent's copy. */
  protected readonly rules = signal<PasswordRulesDto | null>(null);

  protected username = this.route.snapshot.queryParamMap.get('u') ?? '';
  protected code = '';
  protected newPassword = '';
  protected confirmPassword = '';

  /** "Show"/"Hide" is a word here, not an eye icon — same convention as LoginComponent's. */
  protected readonly reveal = signal(false);

  /** Same wording as LoginComponent's: only what the user pool enforces. */
  protected readonly passwordHint = computed(() => {
    const r = this.rules();
    if (!r) return null;
    return r.requireDigit
      ? `Use at least ${r.minLength} characters, including a number.`
      : `Use at least ${r.minLength} characters.`;
  });

  constructor() {
    this.auth.passwordRules().subscribe({
      next: (r) => this.rules.set(r),
      error: () => {},
    });
  }

  protected mismatched(): boolean {
    return this.confirmPassword.length > 0 && this.newPassword !== this.confirmPassword;
  }

  protected toggleReveal(): void {
    this.reveal.update((v) => !v);
  }

  /** Same reasoning as LoginComponent's: one tick per rule the pool actually enforces. */
  protected passwordChecks(): boolean[] {
    const r = this.rules();
    if (!r) return [];

    const checks = [this.newPassword.length >= r.minLength];
    if (r.requireDigit) checks.push(/[0-9]/.test(this.newPassword));
    return checks;
  }

  submit(): void {
    this.error.set(null);
    this.notice.set(null);
    this.busy.set(true);

    this.auth.resetPassword(this.username.trim(), this.code.trim(), this.newPassword).subscribe({
      next: () => {
        this.busy.set(false);
        this.status.set('succeeded');
      },
      error: (err) => {
        this.busy.set(false);
        this.error.set(authError(err, 'Could not reset your password. Please try again.'));
      },
    });
  }

  /** A reset code lasts an hour; asking again is the same request that sent the first. */
  resend(): void {
    this.error.set(null);
    this.notice.set(null);
    this.busy.set(true);

    this.auth.forgotPassword(this.username.trim()).subscribe({
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
