import { Component, computed, inject, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { ActivatedRoute, Router } from '@angular/router';
import { AuthService } from '../../core/auth.service';
import { CognitoError, authError } from '../../core/cognito';
import { PasswordRulesDto } from '../../core/models';

@Component({
  selector: 'app-login',
  imports: [FormsModule],
  templateUrl: './login.component.html',
  styleUrl: './login.component.css',
})
export class LoginComponent {
  private readonly auth = inject(AuthService);
  private readonly router = inject(Router);
  private readonly route = inject(ActivatedRoute);

  protected readonly mode = signal<'login' | 'register' | 'forgot'>('login');
  protected readonly busy = signal(false);
  protected readonly error = signal<string | null>(null);

  /** "Show"/"Hide" is a word here, not an eye icon (design handoff). */
  protected readonly reveal = signal(false);

  protected forgotName = '';

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

    // Only what the user pool enforces (DEPLOYMENT.md § Phase 2, password policy) — a hint for a
    // rule nobody checks would be noise.
    return r.requireDigit
      ? `Use at least ${r.minLength} characters, including a number.`
      : `Use at least ${r.minLength} characters.`;
  });

  /** The pass card's header row — same object the profile badge becomes once it is issued. */
  protected readonly cardKicker = computed(() => {
    if (this.mode() === 'forgot') return 'Lost pass · replacement';
    return this.mode() === 'login' ? 'Admit one · returning' : 'New pass · application';
  });

  /**
   * "No. 04291" for login is flavour, not a real serial — there is no account to derive one from
   * before signing in succeeds. Every other mode has genuinely no serial yet.
   */
  protected readonly cardSerial = computed(() => (this.mode() === 'login' ? 'No. 04291' : 'No. — —'));

  protected readonly modeTitle = computed(() => {
    if (this.mode() === 'forgot') return 'Reset your password';
    return this.mode() === 'login' ? 'Sign in to Marquee' : 'Create your account';
  });

  protected readonly modeSub = computed(() => {
    if (this.mode() === 'forgot') return "Enter your username or email and we'll send a code to reset it.";
    return 'Four times a day a Premiere appears. Clap together to open it.';
  });

  protected readonly submitLabel = computed(() => {
    if (this.mode() === 'forgot') return 'Send reset code';
    return this.mode() === 'login' ? 'Sign in' : 'Register';
  });

  constructor() {
    // ?mode=register opens on the register form, so the shell's "Create account" lands on the form
    // it names rather than on sign-in with a toggle still to find. Read once from the snapshot: the
    // route is never navigated to with a different mode while this component is alive.
    if (this.route.snapshot.queryParamMap.get('mode') === 'register') {
      this.mode.set('register');
    }

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

  openForgotPassword(): void {
    this.mode.set('forgot');
    this.clearErrors();
  }

  backToSignIn(): void {
    this.mode.set('login');
    this.forgotName = '';
    this.clearErrors();
  }

  /**
   * Moves on to the code page whether or not the account exists: the pool answers the same either
   * way (issue #31), so there is nothing here to branch on.
   */
  requestReset(): void {
    this.clearErrors();
    this.busy.set(true);
    const name = this.forgotName.trim();

    this.auth.forgotPassword(name).subscribe({
      next: () => {
        this.busy.set(false);
        this.router.navigate(['/reset-password'], { queryParams: { u: name } });
      },
      error: (err) => {
        this.busy.set(false);
        this.error.set(authError(err, 'Something went wrong. Please try again.'));
      },
    });
  }

  protected toggleReveal(): void {
    this.reveal.update((v) => !v);
  }

  /**
   * One tick per rule the server actually enforces — never the fixed four of the design handoff,
   * which invented case-mixing and symbol requirements that PasswordRulesDto doesn't have. A tick
   * for a rule nobody requires would tell the person to do work the server never asks for.
   */
  protected passwordChecks(): boolean[] {
    const r = this.rules();
    if (!r) return [];

    const checks = [this.password.length >= r.minLength];
    if (r.requireDigit) checks.push(/[0-9]/.test(this.password));
    return checks;
  }

  /**
   * The pool signs in by username or email, so a username that looks like an email would be
   * ambiguous — Cognito refuses it. Said now, with the reason, rather than as a vague refusal later.
   */
  protected usernameLooksLikeEmail(): boolean {
    return this.mode() === 'register' && this.username.includes('@');
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
    const username = this.username.trim();

    const onError = (err: unknown): void => {
      this.busy.set(false);
      // Signing in before confirming is not a failure to report but a step still to take: the code
      // page finishes it, and signs in from there (AuthService keeps the credentials for that).
      if (err instanceof CognitoError && err.type === 'UserNotConfirmedException') {
        this.goConfirm(username);
        return;
      }
      this.error.set(authError(err, 'Something went wrong. Please try again.'));
    };

    if (this.mode() === 'login') {
      this.auth
        .signIn(username, this.password)
        .subscribe({ next: () => this.router.navigate(['/premiere']), error: onError });
    } else {
      // Cognito emails a code; the account cannot sign in until it is entered (decision 3).
      this.auth
        .signUp(username, this.email.trim(), this.password)
        .subscribe({ next: () => this.goConfirm(username), error: onError });
    }
  }

  private goConfirm(username: string): void {
    this.router.navigate(['/confirm-email'], { queryParams: { u: username } });
  }

  private clearErrors(): void {
    this.error.set(null);
  }
}
