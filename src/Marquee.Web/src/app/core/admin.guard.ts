import { inject } from '@angular/core';
import { CanActivateFn, Router } from '@angular/router';
import { AuthService } from './auth.service';

/**
 * Keeps the operations area out of a non-admin's way. This is a convenience, not a security control —
 * the API enforces the same thing with a permission policy and answers 403 regardless of what the
 * SPA decides to render.
 *
 * It reads permissions out of the token without verifying its signature, which is fine for choosing
 * what to draw and fine for nothing else. Anyone can hand themselves a token that renders these
 * screens; none of the data behind them will load.
 */
export const adminGuard: CanActivateFn = () => {
  const auth = inject(AuthService);
  const router = inject(Router);
  if (!auth.isLoggedIn()) return router.createUrlTree(['/login']);
  return auth.canSeeOperations() ? true : router.createUrlTree(['/premiere']);
};

/**
 * Guards one tab on the permission it actually needs, so a future role holding only some of them
 * lands on the part it can use instead of a page that 403s.
 */
export function requirePermission(permission: string): CanActivateFn {
  return () => {
    const auth = inject(AuthService);
    const router = inject(Router);
    if (!auth.isLoggedIn()) return router.createUrlTree(['/login']);
    return auth.has(permission) ? true : router.createUrlTree(['/premiere']);
  };
}
