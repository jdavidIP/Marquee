import { inject } from '@angular/core';
import { CanActivateFn, Router } from '@angular/router';
import { AuthService } from './auth.service';

/**
 * Keeps the dashboard out of a non-admin's way. This is a convenience, not a security control —
 * the API enforces the same thing with a permission policy and answers 403 regardless of what the
 * SPA decides to render.
 */
export const adminGuard: CanActivateFn = () => {
  const auth = inject(AuthService);
  const router = inject(Router);
  if (!auth.isLoggedIn()) return router.createUrlTree(['/login']);
  return auth.isAdmin() ? true : router.createUrlTree(['/premiere']);
};
