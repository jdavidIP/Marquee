import { TestBed } from '@angular/core/testing';
import { ActivatedRoute, convertToParamMap, provideRouter } from '@angular/router';
import { HttpErrorResponse } from '@angular/common/http';
import { Observable, of, throwError } from 'rxjs';
import { ConfirmEmailComponent } from './confirm-email.component';
import { AuthService } from '../../core/auth.service';

describe('ConfirmEmailComponent', () => {
  let confirmSpy: jasmine.Spy;

  function make(token: string | null, confirmResult?: () => Observable<{ message: string }>) {
    TestBed.resetTestingModule();

    confirmSpy = jasmine
      .createSpy('confirmEmail')
      .and.returnValue(confirmResult ? confirmResult() : of({ message: 'Email confirmed.' }));

    TestBed.configureTestingModule({
      imports: [ConfirmEmailComponent],
      providers: [
        provideRouter([]),
        { provide: AuthService, useValue: { confirmEmail: confirmSpy } },
        {
          provide: ActivatedRoute,
          useValue: { snapshot: { queryParamMap: convertToParamMap(token ? { token } : {}) } },
        },
      ],
    });

    const fixture = TestBed.createComponent(ConfirmEmailComponent);
    fixture.detectChanges();
    return fixture.componentInstance as unknown as Record<string, any>;
  }

  it('confirms the token it was handed and reports success', () => {
    const c = make('a-valid-token');

    expect(confirmSpy).toHaveBeenCalledWith('a-valid-token');
    expect(c['status']()).toBe('confirmed');
  });

  it("relays the server's failure message rather than a generic one", () => {
    const c = make('an-expired-token', () =>
      throwError(
        () =>
          new HttpErrorResponse({
            status: 400,
            error: { error: 'This confirmation link is invalid or has expired.' },
          }),
      ),
    );

    expect(c['status']()).toBe('failed');
    expect(c['error']()).toBe('This confirmation link is invalid or has expired.');
  });

  it('fails immediately with no token in the URL, without calling the API', () => {
    const c = make(null);

    expect(confirmSpy).not.toHaveBeenCalled();
    expect(c['status']()).toBe('failed');
  });
});
