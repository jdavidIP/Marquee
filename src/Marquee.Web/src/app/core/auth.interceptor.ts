import { HttpInterceptorFn } from '@angular/common/http';
import { inject } from '@angular/core';
import { AuthService } from './auth.service';
import { AnonymousSessionService } from './anonymous-session.service';

/**
 * Identifies the caller on every API request: the JWT when signed in, otherwise the anonymous
 * session token (Iteration 5).
 *
 * Never both. The server treats a signed-in user as the identity that counts, so sending a leftover
 * anonymous session alongside a bearer token would be noise at best — and at worst an invitation to
 * clap twice for one Premiere, once under each identity.
 */
export const authInterceptor: HttpInterceptorFn = (req, next) => {
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
