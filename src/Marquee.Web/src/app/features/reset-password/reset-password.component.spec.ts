import { TestBed } from '@angular/core/testing';
import { ActivatedRoute, convertToParamMap, provideRouter } from '@angular/router';
import { HttpErrorResponse } from '@angular/common/http';
import { Observable, of, throwError } from 'rxjs';
import { ResetPasswordComponent } from './reset-password.component';
import { AuthService } from '../../core/auth.service';
import { PasswordRulesDto } from '../../core/models';

describe('ResetPasswordComponent', () => {
  let resetSpy: jasmine.Spy;

  const rules: PasswordRulesDto = {
    minLength: 12,
    maxLength: 128,
    requireLetter: true,
    requireDigit: true,
  };

  function make(
    token: string | null,
    resetResult?: () => Observable<{ message: string }>,
  ) {
    TestBed.resetTestingModule();

    resetSpy = jasmine
      .createSpy('resetPassword')
      .and.returnValue(resetResult ? resetResult() : of({ message: 'Password reset.' }));

    TestBed.configureTestingModule({
      imports: [ResetPasswordComponent],
      providers: [
        provideRouter([]),
        {
          provide: AuthService,
          useValue: { resetPassword: resetSpy, passwordRules: () => of(rules) },
        },
        {
          provide: ActivatedRoute,
          useValue: { snapshot: { queryParamMap: convertToParamMap(token ? { token } : {}) } },
        },
      ],
    });

    const fixture = TestBed.createComponent(ResetPasswordComponent);
    fixture.detectChanges();
    return fixture.componentInstance as unknown as Record<string, any>;
  }

  it('shows the form with no token spent yet, given a token in the URL', () => {
    const c = make('a-valid-token');

    expect(c['status']()).toBe('form');
    expect(resetSpy).not.toHaveBeenCalled();
  });

  it('fails immediately with no token in the URL, without a form to submit', () => {
    const c = make(null);

    expect(c['status']()).toBe('invalid');
  });

  it('states the rules it was given, same as the registration form', () => {
    const c = make('a-valid-token');

    expect(c['passwordHint']()).toContain('12');
    expect(c['passwordHint']()).toContain('a number');
  });

  it('catches a mistyped confirmation without asking the server', () => {
    const c = make('a-valid-token');
    c['newPassword'] = 'correct horse battery staple 7';
    c['confirmPassword'] = 'correct horse battery staple';

    expect(c['mismatched']()).toBe(true);

    c['confirmPassword'] = 'correct horse battery staple 7';
    expect(c['mismatched']()).toBe(false);
  });

  it('submits the token alongside the new password and reports success', () => {
    const c = make('a-valid-token');
    c['newPassword'] = 'correct horse battery staple 7';
    c['confirmPassword'] = 'correct horse battery staple 7';

    c['submit']();

    expect(resetSpy).toHaveBeenCalledWith(
      'a-valid-token',
      'correct horse battery staple 7',
      'correct horse battery staple 7',
    );
    expect(c['status']()).toBe('succeeded');
  });

  it('lists every rule the server refused the new password on', () => {
    const c = make('a-valid-token', () =>
      throwError(
        () =>
          new HttpErrorResponse({
            status: 400,
            error: {
              error: 'Use at least 12 characters.',
              problems: [{ rule: 'TooShort', message: 'Use at least 12 characters.' }],
            },
          }),
      ),
    );
    c['newPassword'] = 'weak';
    c['confirmPassword'] = 'weak';

    c['submit']();

    expect(c['status']()).toBe('form');
    expect(c['problems']().map((p: { rule: string }) => p.rule)).toEqual(['TooShort']);
  });

  it("relays the server's failure message for a dead token", () => {
    const c = make('a-used-token', () =>
      throwError(
        () =>
          new HttpErrorResponse({
            status: 400,
            error: { error: 'This reset link is invalid, expired, or already used.' },
          }),
      ),
    );
    c['newPassword'] = 'correct horse battery staple 7';
    c['confirmPassword'] = 'correct horse battery staple 7';

    c['submit']();

    expect(c['error']()).toBe('This reset link is invalid, expired, or already used.');
  });
});
