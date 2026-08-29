import { TestBed } from '@angular/core/testing';
import { provideRouter, Router } from '@angular/router';
import { HttpErrorResponse } from '@angular/common/http';
import { of, throwError } from 'rxjs';
import { LoginComponent } from './login.component';
import { AuthService } from '../../core/auth.service';
import { PasswordRulesDto } from '../../core/models';

/**
 * The registration half of this form gained real responsibilities with issue #27: it has to state
 * the rules before anything is typed, catch a mistyped confirmation without spending a round trip,
 * and list the rules the server refused on rather than running them into one sentence.
 *
 * What it must NOT do is decide anything. Every rule here comes from the API — the assertions below
 * are about relaying, not about validating.
 */
describe('LoginComponent', () => {
  let registerSpy: jasmine.Spy;
  let loginSpy: jasmine.Spy;
  let forgotPasswordSpy: jasmine.Spy;

  const rules: PasswordRulesDto = {
    minLength: 12,
    maxLength: 128,
    requireLetter: true,
    requireDigit: true,
  };

  function make(
    options: {
      rules?: PasswordRulesDto | (() => ReturnType<typeof throwError>);
      registerResult?: (() => ReturnType<typeof throwError>) | null;
      forgotPasswordResult?: () => ReturnType<typeof throwError>;
    } = {},
  ) {
    TestBed.resetTestingModule();

    const rulesResult = options.rules ?? rules;
    registerSpy = jasmine
      .createSpy('register')
      .and.returnValue(
        options.registerResult ? options.registerResult() : of({ token: 't', user: {} }),
      );
    loginSpy = jasmine.createSpy('login').and.returnValue(of({ token: 't', user: {} }));
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
            register: registerSpy,
            login: loginSpy,
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

  it('states the rules it was given rather than rules of its own', () => {
    const c = make();

    expect(c['passwordHint']()).toContain('12');
    expect(c['passwordHint']()).toContain('a number');
  });

  it('reflects a retuned policy without a code change', () => {
    const c = make({ rules: { minLength: 16, maxLength: 128, requireLetter: true, requireDigit: false } });

    expect(c['passwordHint']()).toContain('16');
    expect(c['passwordHint']()).not.toContain('a number');
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

  it('sends the confirmation along with the password', () => {
    const c = make();
    c['mode'].set('register');
    c['username'] = ' ana ';
    c['email'] = ' ana@marquee.test ';
    c['password'] = 'correct horse battery staple 7';
    c['confirmPassword'] = 'correct horse battery staple 7';

    c['submit']();

    expect(registerSpy).toHaveBeenCalledWith(
      'ana',
      'ana@marquee.test',
      'correct horse battery staple 7',
      'correct horse battery staple 7',
    );
  });

  it('shows the check-your-email panel instead of navigating straight in (issue #47)', () => {
    const c = make();
    c['mode'].set('register');
    c['username'] = 'ana';
    c['email'] = 'ana@marquee.test';
    c['password'] = 'correct horse battery staple 7';
    c['confirmPassword'] = 'correct horse battery staple 7';
    const router = TestBed.inject(Router);
    const navigateSpy = spyOn(router, 'navigate');

    c['submit']();

    expect(c['justRegistered']()).toBe(true);
    expect(c['busy']()).toBe(false);
    // Confirming is not a gate — nothing here should have sent the browser anywhere on its own.
    expect(navigateSpy).not.toHaveBeenCalled();
  });

  it('continue() leaves the panel for the app itself', () => {
    const c = make();
    const router = TestBed.inject(Router);
    const navigateSpy = spyOn(router, 'navigate');

    c['continue']();

    expect(navigateSpy).toHaveBeenCalledWith(['/premiere']);
  });

  it('lists every rule the server refused on', () => {
    const c = make({
      registerResult: () =>
        throwError(
          () =>
            new HttpErrorResponse({
              status: 400,
              error: {
                error: 'Use at least 12 characters. Include at least one number.',
                problems: [
                  { rule: 'TooShort', message: 'Use at least 12 characters.' },
                  { rule: 'NoDigit', message: 'Include at least one number.' },
                ],
              },
            }),
        ),
    });
    c['mode'].set('register');

    c['submit']();

    expect(c['problems']().map((p: { rule: string }) => p.rule)).toEqual(['TooShort', 'NoDigit']);
  });

  it('falls back to the single line for a failure that itemises nothing', () => {
    const c = make({
      registerResult: () =>
        throwError(
          () =>
            new HttpErrorResponse({
              status: 409,
              error: { error: 'Username or email is already taken.' },
            }),
        ),
    });
    c['mode'].set('register');

    c['submit']();

    expect(c['problems']()).toEqual([]);
    expect(c['error']()).toBe('Username or email is already taken.');
  });

  it('clears a previous refusal when switching between signing in and registering', () => {
    const c = make({
      registerResult: () =>
        throwError(
          () =>
            new HttpErrorResponse({
              status: 400,
              error: { error: 'too short', problems: [{ rule: 'TooShort', message: 'too short' }] },
            }),
        ),
    });
    c['mode'].set('register');
    c['confirmPassword'] = 'something';
    c['submit']();
    expect(c['problems']().length).toBe(1);

    c['toggle']();

    expect(c['problems']()).toEqual([]);
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
