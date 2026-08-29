import { Component, computed, inject, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { ActivatedRoute, RouterLink } from '@angular/router';
import { AuthService } from '../../core/auth.service';
import { apiError, passwordProblems } from '../../core/http-error';
import { PasswordProblemDto, PasswordRulesDto } from '../../core/models';

type ResetStatus = 'form' | 'succeeded' | 'invalid';

/**
 * What the emailed reset-password link opens (issue #50). Unlike ConfirmEmailComponent this can
 * never resolve itself on load — setting a password needs a form — so the token is only read once,
 * up front, and held for the eventual submit rather than acted on immediately.
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

  private readonly token = this.route.snapshot.queryParamMap.get('token');

  protected readonly status = signal<ResetStatus>(this.token ? 'form' : 'invalid');
  protected readonly busy = signal(false);
  protected readonly error = signal<string | null>(
    this.token ? null : 'This reset link is missing its token.',
  );
  protected readonly problems = signal<PasswordProblemDto[]>([]);

  /** Same "fetch once, degrade gracefully if it fails" reasoning as LoginComponent's copy. */
  protected readonly rules = signal<PasswordRulesDto | null>(null);

  protected newPassword = '';
  protected confirmPassword = '';

  protected readonly passwordHint = computed(() => {
    const r = this.rules();
    if (!r) return null;

    const parts = [`at least ${r.minLength} characters`];
    if (r.requireLetter) parts.push('a letter');
    if (r.requireDigit) parts.push('a number');

    return `Use ${parts.join(', ')}. Avoid your username and anything widely used.`;
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

  submit(): void {
    // The form is not rendered without a token (status would be 'invalid'), so this only guards
    // against a stray call — the real gate is the template.
    if (!this.token) return;

    this.error.set(null);
    this.problems.set([]);
    this.busy.set(true);

    this.auth.resetPassword(this.token, this.newPassword, this.confirmPassword).subscribe({
      next: () => {
        this.busy.set(false);
        this.status.set('succeeded');
      },
      error: (err) => {
        this.busy.set(false);
        this.problems.set(passwordProblems(err));
        this.error.set(apiError(err, 'This reset link is invalid, expired, or already used.'));
      },
    });
  }
}
