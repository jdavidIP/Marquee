import { HttpErrorResponse, HttpInterceptorFn } from '@angular/common/http';
import { inject } from '@angular/core';
import { AuthService } from './auth.service';
import { AnonymousSessionService } from './anonymous-session.service';
import { environment } from '../../environments/environment';
import { catchError, throwError } from 'rxjs';

/**
 * Identifies the caller on every API request: the JWT when signed in, otherwise the anonymous
 * session token (Iteration 5).
 *
 * Never both. The server treats a signed-in user as the identity that counts, so sending a leftover
 * anonymous session alongside a bearer token would be noise at best — and at worst an invitation to
 * clap twice for one Premiere, once under each identity.
 *
 * Only on requests to Marquee's own API. The user pool is called from the browser too (CognitoClient),
 * and neither credential is any of its business — nor does its CORS policy allow the headers.
 */
export const authInterceptor: HttpInterceptorFn = (req, next) => {
  if (!req.url.startsWith(environment.apiBase)) return next(req);

  const auth = inject(AuthService);
  const sessions = inject(AnonymousSessionService);
  const token = auth.token;
  if (token) {
    // A 401 to a request that carried our token means the API no longer accepts it — it expired, or
    // predates the move to Cognito. Signing out locally turns "looks signed in, can do nothing" into
    // an ordinary visitor, with a visitor session so clapping still works.
    return next(req.clone({ setHeaders: { Authorization: `Bearer ${token}` } })).pipe(
      catchError((err: HttpErrorResponse) => {
        if (err.status === 401 && auth.token === token) {
          auth.logout();
          void sessions.ensure();
        }
        return throwError(() => err);
      }),
    );
  }

  const anonToken = sessions.token;
  if (anonToken) {
    return next(req.clone({ setHeaders: { 'X-Anon-Session': anonToken } }));
  }

  return next(req);
};
