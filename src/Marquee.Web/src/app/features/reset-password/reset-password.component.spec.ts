import { TestBed } from '@angular/core/testing';
import { ActivatedRoute, convertToParamMap, provideRouter } from '@angular/router';
import { Observable, of, throwError } from 'rxjs';
import { ResetPasswordComponent } from './reset-password.component';
import { AuthService } from '../../core/auth.service';
import { CognitoError } from '../../core/cognito';
import { PasswordRulesDto } from '../../core/models';

describe('ResetPasswordComponent', () => {
  let resetSpy: jasmine.Spy;
  let forgotSpy: jasmine.Spy;

  const rules: PasswordRulesDto = {
    minLength: 10,
    maxLength: 128,
    requireLetter: false,
    requireDigit: true,
  };

  function make(
    u: string | null,
    options: { reset?: () => Observable<void>; forgot?: () => Observable<void> } = {},
  ) {
    TestBed.resetTestingModule();

    resetSpy = jasmine
      .createSpy('resetPassword')
      .and.returnValue(options.reset ? options.reset() : of(undefined));
    forgotSpy = jasmine
      .createSpy('forgotPassword')
      .and.returnValue(options.forgot ? options.forgot() : of(undefined));

    TestBed.configureTestingModule({
      imports: [ResetPasswordComponent],
      providers: [
        provideRouter([]),
        {
          provide: AuthService,
          useValue: {
            resetPassword: resetSpy,
            forgotPassword: forgotSpy,
            passwordRules: () => of(rules),
          },
        },
        {
          provide: ActivatedRoute,
          useValue: { snapshot: { queryParamMap: convertToParamMap(u ? { u } : {}) } },
        },
      ],
    });

    const fixture = TestBed.createComponent(ResetPasswordComponent);
    fixture.detectChanges();
    return fixture.componentInstance as unknown as Record<string, any>;
  }

  it('starts from the account it was sent with, and works opened by hand', () => {
    expect(make('ana')['username']).toBe('ana');
    expect(make(null)['username']).toBe('');
  });

  it('resets with the code and the new password, then offers sign-in', () => {
    const c = make('ana');
    c['code'] = ' 123456 ';
    c['newPassword'] = 'a new password 7';

    c['submit']();

    expect(resetSpy).toHaveBeenCalledWith('ana', '123456', 'a new password 7');
    expect(c['status']()).toBe('succeeded');
  });

  it('says an expired code in plain words and keeps the form', () => {
    const c = make('ana', {
      reset: () => throwError(() => new CognitoError('ExpiredCodeException', 'Invalid code provided')),
    });

    c['submit']();

    expect(c['status']()).toBe('form');
    expect(c['error']()).toBe('That code has expired. Send a new one.');
    expect(c['busy']()).toBe(false);
  });

  it('relays a password the pool refuses', () => {
    const c = make('ana', {
      reset: () =>
        throwError(() => new CognitoError('InvalidPasswordException', 'Password not long enough')),
    });

    c['submit']();

    expect(c['error']()).toBe('Use at least 10 characters, including a number.');
  });

  it('sends a new code by asking again', () => {
    const c = make('ana');

    c['resend']();

    expect(forgotSpy).toHaveBeenCalledWith('ana');
    expect(c['notice']()).toContain('new code');
  });

  it('hints only the rules the pool enforces', () => {
    const c = make('ana');
    c['newPassword'] = 'no digits here';

    expect(c['passwordHint']()).toBe('Use at least 10 characters, including a number.');
    expect(c['passwordChecks']()).toEqual([true, false]);
  });

  it('catches a mistyped confirmation without asking the pool', () => {
    const c = make('ana');
    c['newPassword'] = 'a new password 7';
    c['confirmPassword'] = 'a new password';

    expect(c['mismatched']()).toBe(true);
  });
});
