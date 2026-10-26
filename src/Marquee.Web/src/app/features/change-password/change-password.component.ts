import { Component, computed, inject, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { RouterLink } from '@angular/router';
import { AuthService } from '../../core/auth/auth.service';
import { CognitoError, authError } from '../../core/auth/cognito';
import { PasswordRulesDto } from '../../core/models';

/**
 * Changing a password you know (#130), reached from your own profile. Talks to the user pool
 * through AuthService, never the API. Someone who has lost their password uses the emailed-code
 * flow on the reset page instead.
 */
@Component({
  selector: 'app-change-password',
  imports: [FormsModule, RouterLink],
  templateUrl: './change-password.component.html',
  styleUrl: './change-password.component.css',
})
export class ChangePasswordComponent {
  protected readonly auth = inject(AuthService);

  protected readonly busy = signal(false);
  protected readonly error = signal<string | null>(null);
  protected readonly changed = signal(false);
  /** "Show"/"Hide" is a word here, not an eye icon — same convention as the sign-in page. */
  protected readonly reveal = signal(false);

  /** Same "fetch once, degrade gracefully if it fails" reasoning as the sign-in and reset pages. */
  protected readonly rules = signal<PasswordRulesDto | null>(null);

  protected oldPassword = '';
  protected newPassword = '';
  protected confirmPassword = '';

  /** Same wording as the sign-in and reset pages: only what the user pool enforces. */
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

  /** One tick per rule the pool actually enforces, as on the reset page. */
  protected passwordChecks(): boolean[] {
    const r = this.rules();
    if (!r) return [];

    const checks = [this.newPassword.length >= r.minLength];
    if (r.requireDigit) checks.push(/[0-9]/.test(this.newPassword));
    return checks;
  }

  protected submit(): void {
    this.error.set(null);
    this.busy.set(true);

    this.auth.changePassword(this.oldPassword, this.newPassword).subscribe({
      next: () => {
        this.busy.set(false);
        this.changed.set(true);
      },
      error: (err: unknown) => {
        this.busy.set(false);
        this.error.set(this.message(err));
      },
    });
  }

  /** The pool's wrong-old-password refusal reads "username or password" elsewhere; here there is no username to get wrong. */
  private message(err: unknown): string {
    if (err instanceof CognitoError && err.type === 'NotAuthorizedException') {
      return 'Your current password is not right.';
    }
    return authError(err, 'Could not change your password. Please try again.');
  }
}
