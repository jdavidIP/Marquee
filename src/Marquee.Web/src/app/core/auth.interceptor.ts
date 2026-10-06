import { HttpInterceptorFn } from '@angular/common/http';
import { inject } from '@angular/core';
import { AuthService } from './auth.service';
import { AnonymousSessionService } from './anonymous-session.service';
import { environment } from '../../environments/environment';

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

  const token = inject(AuthService).token;
  if (token) {
    return next(req.clone({ setHeaders: { Authorization: `Bearer ${token}` } }));
  }

  const anonToken = inject(AnonymousSessionService).token;
  if (anonToken) {
    return next(req.clone({ setHeaders: { 'X-Anon-Session': anonToken } }));
  }

  return next(req);
};
