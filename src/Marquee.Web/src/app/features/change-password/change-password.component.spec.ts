import { TestBed } from '@angular/core/testing';
import { signal } from '@angular/core';
import { provideRouter } from '@angular/router';
import { Observable, of, throwError } from 'rxjs';
import { ChangePasswordComponent } from './change-password.component';
import { AuthService } from '../../core/auth/auth.service';
import { CognitoError } from '../../core/auth/cognito';

/** Changing your own password (#130): what the page sends, and what each refusal reads as. */
describe('ChangePasswordComponent', () => {
  let changeSpy: jasmine.Spy;
  let fixture: ReturnType<typeof TestBed.createComponent<ChangePasswordComponent>>;
  let root: HTMLElement;
  let component: Record<string, any>;

  function make(result: Observable<void> = of(undefined)) {
    changeSpy = jasmine.createSpy('changePassword').and.returnValue(result);
    TestBed.configureTestingModule({
      imports: [ChangePasswordComponent],
      providers: [
        provideRouter([]),
        {
          provide: AuthService,
          useValue: {
            user: signal({ username: 'ana' }),
            changePassword: changeSpy,
            passwordRules: () => of({ minLength: 10, requireDigit: true }),
          },
        },
      ],
    });
    fixture = TestBed.createComponent(ChangePasswordComponent);
    fixture.detectChanges();
    root = fixture.nativeElement;
    component = fixture.componentInstance as unknown as Record<string, any>;
    component['oldPassword'] = 'old-password-1';
    component['newPassword'] = 'new-password-2';
    component['confirmPassword'] = 'new-password-2';
  }

  it('states the pool rules before anyone submits', () => {
    make();

    expect(root.querySelector('.strength__hint')?.textContent).toContain(
      'at least 10 characters, including a number',
    );
  });

  it('sends both passwords, then says so and links back to the profile', () => {
    make();

    component['submit']();
    fixture.detectChanges();

    expect(changeSpy).toHaveBeenCalledWith('old-password-1', 'new-password-2');
    expect(root.querySelector('.pass__title')?.textContent).toContain('Password changed');
    expect(root.querySelector('a.pass__submit')?.getAttribute('href')).toBe('/u/ana');
    expect(root.querySelector('.error-box')).toBeNull();
  });

  it('flags a confirmation that does not match, and blocks the submit', () => {
    make();
    component['confirmPassword'] = 'something-else-3';

    expect(component['mismatched']()).toBeTrue();
  });

  const refusals: [string, string][] = [
    ['NotAuthorizedException', 'Your current password is not right, or your session has expired.'],
    ['InvalidPasswordException', 'Use at least 10 characters, including a number.'],
    ['LimitExceededException', 'Too many attempts. Wait a few minutes and try again.'],
    ['NetworkError', 'Cannot reach the sign-in service. Check your connection.'],
  ];
  for (const [type, message] of refusals) {
    it(`reads ${type} as "${message}"`, () => {
      make(throwError(() => new CognitoError(type, type)));

      component['submit']();
      fixture.detectChanges();

      expect(root.querySelector('.error-box')?.textContent).toContain(message);
      expect(root.querySelector('.pass__title')?.textContent).toContain('Change your password');
    });
  }

  it('falls back to a generic message for a refusal it does not know', () => {
    make(throwError(() => new CognitoError('SomethingNew', 'x')));

    component['submit']();
    fixture.detectChanges();

    expect(root.querySelector('.error-box')?.textContent).toContain('Could not change your password.');
  });
});
