import { Component, computed, inject, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { Router } from '@angular/router';
import { AuthService } from '../../core/auth.service';
import { apiError, passwordProblems } from '../../core/http-error';
import { PasswordProblemDto, PasswordRulesDto } from '../../core/models';

@Component({
  selector: 'app-login',
  imports: [FormsModule],
  templateUrl: './login.component.html',
  styleUrl: './login.component.css',
})
export class LoginComponent {
  private readonly auth = inject(AuthService);
  private readonly router = inject(Router);

  protected readonly mode = signal<'login' | 'register'>('login');
  protected readonly busy = signal(false);
  protected readonly error = signal<string | null>(null);

  /**
   * Shown in place of the form after a successful registration, instead of navigating straight to
   * /premiere (issue #47) — registration creates an unconfirmed account, and leaving with no mention
   * of that was the gap #47's own text called out. The account already works meanwhile (an
   * unconfirmed user claps as an anonymous participant — issue #29), so this never blocks entry,
   * it only makes sure the person knows there is a link waiting in their inbox.
   */
  protected readonly justRegistered = signal(false);

  /** The rules the server refused on, itemised — the same content as error(), listed instead. */
  protected readonly problems = signal<PasswordProblemDto[]>([]);

  /**
   * Null until the API answers, and it may stay null: this only drives a hint and the browser's own
   * minlength, so the form still works unaided if the call fails. The server is the authority
   * either way, which is why nothing here is a second copy of the rules.
   */
  protected readonly rules = signal<PasswordRulesDto | null>(null);

  protected username = '';
  protected email = '';
  protected password = '';
  protected confirmPassword = '';

  /** Says what will be accepted before anything is typed, rather than after it is rejected. */
  protected readonly passwordHint = computed(() => {
    const r = this.rules();
    if (!r) return null;

    const parts = [`at least ${r.minLength} characters`];
    if (r.requireLetter) parts.push('a letter');
    if (r.requireDigit) parts.push('a number');

    return `Use ${parts.join(', ')}. Avoid your username and anything widely used.`;
  });

  constructor() {
    // Fetched once for the session rather than on entering register mode: it is a few bytes, it
    // never changes while the page is open, and asking for it up front means the hint is already
    // there the moment the form switches.
    this.auth.passwordRules().subscribe({
      next: (r) => this.rules.set(r),
      error: () => {},
    });
  }

  toggle(): void {
    this.mode.set(this.mode() === 'login' ? 'register' : 'login');
    this.clearErrors();
    // Deliberately not carried across: a confirmation is only meaningful next to the password it
    // was typed against, and leaving it filled would let a stale value satisfy the check.
    this.confirmPassword = '';
  }

  /** Both typed and different — worth saying now rather than spending a round trip on it. */
  protected mismatched(): boolean {
    return (
      this.mode() === 'register' &&
      this.confirmPassword.length > 0 &&
      this.password !== this.confirmPassword
    );
  }

  submit(): void {
    this.clearErrors();
    this.busy.set(true);

    const onError = (err: unknown): void => {
      this.busy.set(false);
      this.problems.set(passwordProblems(err));
      this.error.set(apiError(err, 'Something went wrong. Please try again.'));
    };

    if (this.mode() === 'login') {
      this.auth
        .login(this.username.trim(), this.password)
        .subscribe({ next: () => this.router.navigate(['/premiere']), error: onError });
    } else {
      this.auth
        .register(this.username.trim(), this.email.trim(), this.password, this.confirmPassword)
        .subscribe({
          // Straight to /premiere would say nothing about the confirmation email just sent — this
          // panel is that message, not an extra gate (see justRegistered's doc comment).
          next: () => {
            this.busy.set(false);
            this.justRegistered.set(true);
          },
          error: onError,
        });
    }
  }

  /** Leaves the "check your email" panel for the app itself — the account already works meanwhile. */
  continue(): void {
    this.router.navigate(['/premiere']);
  }

  private clearErrors(): void {
    this.error.set(null);
    this.problems.set([]);
  }
}
