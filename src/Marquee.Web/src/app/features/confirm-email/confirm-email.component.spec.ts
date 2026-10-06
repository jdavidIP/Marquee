import { TestBed } from '@angular/core/testing';
import { ActivatedRoute, Router, convertToParamMap, provideRouter } from '@angular/router';
import { Observable, of, throwError } from 'rxjs';
import { ConfirmEmailComponent } from './confirm-email.component';
import { AuthService } from '../../core/auth.service';
import { CognitoError } from '../../core/cognito';

describe('ConfirmEmailComponent', () => {
  let confirmSpy: jasmine.Spy;
  let resendSpy: jasmine.Spy;

  function make(
    u: string | null,
    options: {
      confirm?: () => Observable<'signed-in' | 'confirmed'>;
      resend?: () => Observable<void>;
    } = {},
  ) {
    TestBed.resetTestingModule();

    confirmSpy = jasmine
      .createSpy('confirmSignUp')
      .and.returnValue(options.confirm ? options.confirm() : of('confirmed'));
    resendSpy = jasmine
      .createSpy('resendCode')
      .and.returnValue(options.resend ? options.resend() : of(undefined));

    TestBed.configureTestingModule({
      imports: [ConfirmEmailComponent],
      providers: [
        provideRouter([]),
        { provide: AuthService, useValue: { confirmSignUp: confirmSpy, resendCode: resendSpy } },
        {
          provide: ActivatedRoute,
          useValue: { snapshot: { queryParamMap: convertToParamMap(u ? { u } : {}) } },
        },
      ],
    });

    const fixture = TestBed.createComponent(ConfirmEmailComponent);
    fixture.detectChanges();
    return fixture.componentInstance as unknown as Record<string, any>;
  }

  it('starts from the username it was sent with', () => {
    expect(make('ana')['username']).toBe('ana');
  });

  it('still works opened by hand, with the username to fill in', () => {
    expect(make(null)['username']).toBe('');
  });

  it('goes straight to the Premiere when confirming also signed in', () => {
    const c = make('ana', { confirm: () => of('signed-in') });
    const navigateSpy = spyOn(TestBed.inject(Router), 'navigate');
    c['code'] = ' 123456 ';

    c['submit']();

    expect(confirmSpy).toHaveBeenCalledWith('ana', '123456');
    expect(navigateSpy).toHaveBeenCalledWith(['/premiere']);
  });

  it('asks the person to sign in when this tab no longer holds their password', () => {
    const c = make('ana', { confirm: () => of('confirmed') });
    c['code'] = '123456';

    c['submit']();

    expect(c['status']()).toBe('confirmed');
  });

  it('says a wrong code in plain words and keeps the form', () => {
    const c = make('ana', {
      confirm: () =>
        throwError(() => new CognitoError('CodeMismatchException', 'Invalid verification code')),
    });
    c['code'] = '000000';

    c['submit']();

    expect(c['status']()).toBe('form');
    expect(c['error']()).toBe('That code is not right. Check it and try again.');
    expect(c['busy']()).toBe(false);
  });

  it('confirms a new code went out', () => {
    const c = make('ana');

    c['resend']();

    expect(resendSpy).toHaveBeenCalledWith('ana');
    expect(c['notice']()).toContain('new code');
    expect(c['error']()).toBeNull();
  });

  it('survives a pool that cannot resend, as cognito-local cannot', () => {
    const c = make('ana', {
      resend: () =>
        throwError(() => new CognitoError('Unsupported', 'Cognito Local unsupported feature')),
    });

    c['resend']();

    expect(c['error']()).toBe('Could not send a new code. Please try again.');
    expect(c['notice']()).toBeNull();
    expect(c['busy']()).toBe(false);
  });
});
