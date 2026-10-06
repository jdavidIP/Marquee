import { HttpClient, HttpErrorResponse, HttpHeaders } from '@angular/common/http';
import { Injectable, inject } from '@angular/core';
import { Observable, catchError, throwError } from 'rxjs';
import { environment } from '../../environments/environment';
import { apiError } from './http-error';

/**
 * A refusal from the user pool, by its error type (e.g. `NotAuthorizedException`) — what decides how
 * the UI reacts, where the message is only Cognito's own wording.
 */
export class CognitoError extends Error {
  constructor(
    readonly type: string,
    message: string,
  ) {
    super(message);
  }
}

/**
 * Calls the Cognito user pool's API directly from the browser (DEPLOYMENT.md § Phase 2, decision 6):
 * unsigned JSON POSTs naming the operation in a header. Through HttpClient rather than Amplify, so
 * these calls are as testable as every other request in the app.
 *
 * Every failure comes out as a {@link CognitoError}, so callers branch on a type rather than on the
 * shape of an HTTP response.
 */
@Injectable({ providedIn: 'root' })
export class CognitoClient {
  private readonly http = inject(HttpClient);

  call<T>(operation: string, body: object): Observable<T> {
    return this.http
      .post<T>(
        environment.cognito.endpoint,
        { ClientId: environment.cognito.clientId, ...body },
        {
          headers: new HttpHeaders({
            'Content-Type': 'application/x-amz-json-1.1',
            'X-Amz-Target': `AWSCognitoIdentityProviderService.${operation}`,
          }),
        },
      )
      .pipe(catchError((err: HttpErrorResponse) => throwError(() => toCognitoError(err))));
  }
}

function toCognitoError(err: HttpErrorResponse): CognitoError {
  const body = err.error as { __type?: string; message?: string } | null;
  // Some types arrive namespaced ("Prefix#NotAuthorizedException"); the part after # is the type.
  const type = body?.__type?.split('#').pop() ?? (err.status === 0 ? 'NetworkError' : 'UnknownError');
  return new CognitoError(type, body?.message ?? err.message);
}

/**
 * What to tell a person about a failed auth step — Cognito's refusals in Marquee's words, anything
 * else (the API's own errors) as {@link apiError} would put it.
 */
export function authError(err: unknown, fallback: string): string {
  if (!(err instanceof CognitoError)) return apiError(err, fallback);

  switch (err.type) {
    case 'NotAuthorizedException':
      return 'Incorrect username or password.';
    case 'UsernameExistsException':
      return 'That username is already taken.';
    case 'AliasExistsException':
      return 'That email is already used by another account.';
    case 'InvalidPasswordException':
      return 'Use at least 10 characters, including a number.';
    case 'CodeMismatchException':
      return 'That code is not right. Check it and try again.';
    case 'ExpiredCodeException':
      return 'That code has expired. Send a new one.';
    case 'LimitExceededException':
    case 'TooManyRequestsException':
    case 'TooManyFailedAttemptsException':
      return 'Too many attempts. Wait a few minutes and try again.';
    case 'NetworkError':
      return 'Cannot reach the sign-in service. Check your connection.';
    default:
      return fallback;
  }
}
