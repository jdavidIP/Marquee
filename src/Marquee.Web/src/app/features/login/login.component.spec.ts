import { TestBed } from '@angular/core/testing';
import { provideRouter, Router } from '@angular/router';
import { HttpErrorResponse } from '@angular/common/http';
import { of, throwError } from 'rxjs';
import { LoginComponent } from './login.component';
import { AuthService } from '../../core/auth.service';
import { CognitoError } from '../../core/cognito';
import { PasswordRulesDto } from '../../core/models';

/**
 * Sign-in and registration go to the Cognito user pool (phase 2, #111) through AuthService; this form
 * states the rules before anything is typed, catches a mistyped confirmation without a round trip,
 * and turns the pool's refusals into Marquee's words. What it must NOT do is decide anything about a
 * password — the hint relays the API's rules, and the pool enforces them.
 */
describe('LoginComponent', () => {
  let signUpSpy: jasmine.Spy;
  let signInSpy: jasmine.Spy;
  let forgotPasswordSpy: jasmine.Spy;

  const rules: PasswordRulesDto = {
    minLength: 12,
    maxLength: 128,
    requireLetter: false,
    requireDigit: true,
  };

  function make(
    options: {
      rules?: PasswordRulesDto | (() => ReturnType<typeof throwError>);
      signUpResult?: () => ReturnType<typeof throwError>;
      signInResult?: () => ReturnType<typeof throwError>;
      forgotPasswordResult?: () => ReturnType<typeof throwError>;
    } = {},
  ) {
    TestBed.resetTestingModule();

    const rulesResult = options.rules ?? rules;
    signUpSpy = jasmine
      .createSpy('signUp')
      .and.returnValue(options.signUpResult ? options.signUpResult() : of(undefined));
    signInSpy = jasmine
      .createSpy('signIn')
      .and.returnValue(options.signInResult ? options.signInResult() : of({}));
    forgotPasswordSpy = jasmine
      .createSpy('forgotPassword')
      .and.returnValue(
        options.forgotPasswordResult
          ? options.forgotPasswordResult()
          : of({ message: 'If that address is registered, a reset link has been sent.' }),
      );

    TestBed.configureTestingModule({
      imports: [LoginComponent],
      providers: [
        provideRouter([]),
        {
          provide: AuthService,
          useValue: {
            signUp: signUpSpy,
            signIn: signInSpy,
            forgotPassword: forgotPasswordSpy,
            passwordRules: () =>
              typeof rulesResult === 'function' ? rulesResult() : of(rulesResult),
          },
        },
      ],
    });

    const fixture = TestBed.createComponent(LoginComponent);
    fixture.detectChanges();
    return fixture.componentInstance as unknown as Record<string, any>;
  }

  function fillRegistration(c: Record<string, any>): void {
    c['mode'].set('register');
    c['username'] = ' ana ';
    c['email'] = ' ana@marquee.test ';
    c['password'] = 'correct horse battery staple 7';
    c['confirmPassword'] = 'correct horse battery staple 7';
  }

  it('states the rules it was given rather than rules of its own', () => {
    const c = make();

    expect(c['passwordHint']()).toBe('Use at least 12 characters, including a number.');
  });

  it('reflects a retuned policy without a code change', () => {
    const c = make({ rules: { minLength: 16, maxLength: 128, requireLetter: false, requireDigit: false } });

    expect(c['passwordHint']()).toContain('16');
    expect(c['passwordHint']()).not.toContain('a number');
  });

  it('checks the password against the real rules, not a fixed set', () => {
    // requireDigit is false here, so a tick for it would tell the person to do work the server
    // never asked for.
    const c = make({
      rules: { minLength: 12, maxLength: 128, requireLetter: false, requireDigit: false },
    });
    c['mode'].set('register');
    c['password'] = 'short1';

    expect(c['passwordChecks']()).toEqual([false]);

    c['password'] = 'long enough now';
    expect(c['passwordChecks']()).toEqual([true]);
  });

  it('still offers the form when the rules cannot be fetched', () => {
    // The server enforces them regardless, so a failed hint must not block registration.
    const c = make({ rules: () => throwError(() => new HttpErrorResponse({ status: 500 })) });

    expect(c['rules']()).toBeNull();
    expect(c['passwordHint']()).toBeNull();
  });

  it('catches a mistyped confirmation without asking the server', () => {
    const c = make();
    c['mode'].set('register');
    c['password'] = 'correct horse battery staple 7';
    c['confirmPassword'] = 'correct horse battery staple';

    expect(c['mismatched']()).toBe(true);

    c['confirmPassword'] = 'correct horse battery staple 7';
    expect(c['mismatched']()).toBe(false);
  });

  it('does not call a half-typed confirmation a mismatch', () => {
    const c = make();
    c['mode'].set('register');
    c['password'] = 'correct horse battery staple 7';
    c['confirmPassword'] = '';

    expect(c['mismatched']()).toBe(false);
  });

  it('never treats signing in as a mismatch, whatever is left in the field', () => {
    const c = make();
    c['password'] = 'one thing';
    c['confirmPassword'] = 'another thing';

    expect(c['mode']()).toBe('login');
    expect(c['mismatched']()).toBe(false);
  });

  it('signs up with the trimmed details and goes to enter the emailed code', () => {
    const c = make();
    fillRegistration(c);
    const navigateSpy = spyOn(TestBed.inject(Router), 'navigate');

    c['submit']();

    expect(signUpSpy).toHaveBeenCalledWith('ana', 'ana@marquee.test', 'correct horse battery staple 7');
    expect(navigateSpy).toHaveBeenCalledWith(['/confirm-email'], { queryParams: { u: 'ana' } });
  });

  it('signs in and goes to the Premiere', () => {
    const c = make();
    c['username'] = ' ana ';
    c['password'] = 'correct horse battery staple 7';
    const navigateSpy = spyOn(TestBed.inject(Router), 'navigate');

    c['submit']();

    expect(signInSpy).toHaveBeenCalledWith('ana', 'correct horse battery staple 7');
    expect(navigateSpy).toHaveBeenCalledWith(['/premiere']);
  });

  it('sends an unconfirmed sign-in to the code page rather than calling it a failure', () => {
    const c = make({
      signInResult: () =>
        throwError(() => new CognitoError('UserNotConfirmedException', 'User is not confirmed.')),
    });
    c['username'] = 'ana';
    c['password'] = 'correct horse battery staple 7';
    const navigateSpy = spyOn(TestBed.inject(Router), 'navigate');

    c['submit']();

    expect(navigateSpy).toHaveBeenCalledWith(['/confirm-email'], { queryParams: { u: 'ana' } });
    expect(c['error']()).toBeNull();
  });

  it("says a wrong password in Marquee's words, not the pool's", () => {
    const c = make({
      signInResult: () =>
        throwError(() => new CognitoError('NotAuthorizedException', 'User not authorized')),
    });

    c['submit']();

    expect(c['error']()).toBe('Incorrect username or password.');
    expect(c['busy']()).toBe(false);
  });

  it('relays a taken username from sign-up', () => {
    const c = make({
      signUpResult: () =>
        throwError(() => new CognitoError('UsernameExistsException', 'User already exists')),
    });
    fillRegistration(c);

    c['submit']();

    expect(c['error']()).toBe('That username is already taken.');
  });

  it('clears a previous refusal when switching between signing in and registering', () => {
    const c = make({
      signUpResult: () =>
        throwError(() => new CognitoError('InvalidPasswordException', 'Password not long enough')),
    });
    fillRegistration(c);
    c['submit']();
    expect(c['error']()).toBe('Use at least 10 characters, including a number.');

    c['toggle']();

    expect(c['error']()).toBeNull();
    // A confirmation only means anything beside the password it was typed against; carrying it
    // across would let a stale value satisfy the check on the way back.
    expect(c['confirmPassword']).toBe('');
  });

  it('shows the server\'s own message after requesting a reset (issue #50)', () => {
    const c = make();
    c['openForgotPassword']();
    c['forgotEmail'] = ' ana@marquee.test ';

    c['requestReset']();

    expect(forgotPasswordSpy).toHaveBeenCalledWith(' ana@marquee.test '.trim());
    expect(c['resetRequested']()).toBe(true);
    expect(c['resetMessage']()).toBe('If that address is registered, a reset link has been sent.');
  });

  it('backToSignIn() clears the request state', () => {
    const c = make();
    c['openForgotPassword']();
    c['forgotEmail'] = 'ana@marquee.test';
    c['requestReset']();
    expect(c['resetRequested']()).toBe(true);

    c['backToSignIn']();

    expect(c['mode']()).toBe('login');
    expect(c['resetRequested']()).toBe(false);
    expect(c['resetMessage']()).toBeNull();
    expect(c['forgotEmail']).toBe('');
  });

  it('surfaces a failed reset request the same way as any other API error', () => {
    const c = make({
      forgotPasswordResult: () => throwError(() => new HttpErrorResponse({ status: 429 })),
    });
    c['openForgotPassword']();
    c['forgotEmail'] = 'ana@marquee.test';

    c['requestReset']();

    expect(c['resetRequested']()).toBe(false);
    expect(c['error']()).toBeTruthy();
  });
});
