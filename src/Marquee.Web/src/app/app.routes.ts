import { Routes } from '@angular/router';
import { authGuard } from './core/auth.guard';
import { adminGuard } from './core/admin.guard';

export const routes: Routes = [
  { path: '', pathMatch: 'full', redirectTo: 'premiere' },
  {
    path: 'login',
    loadComponent: () => import('./features/login/login.component').then((m) => m.LoginComponent),
  },
  {
    // No authGuard: this is exactly what someone without a session yet needs to open (issue #47).
    path: 'confirm-email',
    loadComponent: () =>
      import('./features/confirm-email/confirm-email.component').then(
        (m) => m.ConfirmEmailComponent,
      ),
  },
  {
    // Same reasoning as confirm-email — reachable with no session, since resetting a password is
    // exactly what someone locked out of their account needs (issue #50).
    path: 'reset-password',
    loadComponent: () =>
      import('./features/reset-password/reset-password.component').then(
        (m) => m.ResetPasswordComponent,
      ),
  },
  {
    path: 'premiere',
    canActivate: [authGuard],
    loadComponent: () => import('./features/premiere/premiere.component').then((m) => m.PremiereComponent),
  },
  {
    path: 'library',
    canActivate: [authGuard],
    loadComponent: () => import('./features/library/library.component').then((m) => m.LibraryComponent),
  },
  {
    path: 'friends',
    canActivate: [authGuard],
    loadComponent: () => import('./features/friends/friends.component').then((m) => m.FriendsComponent),
  },
  {
    // /u/:username rather than /users/:username — short enough to read aloud, and it keeps the
    // route distinct from the admin users screen.
    path: 'u/:username',
    canActivate: [authGuard],
    loadComponent: () => import('./features/profile/profile.component').then((m) => m.ProfileComponent),
  },
  {
    // Nested under the profile route rather than /friends/:username — this is that user's friend
    // list, not a variant of the signed-in user's own /friends screen.
    path: 'u/:username/friends',
    canActivate: [authGuard],
    loadComponent: () =>
      import('./features/friends/user-friends.component').then((m) => m.UserFriendsComponent),
  },
  {
    // LibraryComponent itself, reused rather than duplicated: the same search/filter/sort screen
    // that /library shows for the caller's own collection now also serves anyone else's, switched
    // by whether a username param is present at all (issue #38).
    path: 'u/:username/library',
    canActivate: [authGuard],
    loadComponent: () => import('./features/library/library.component').then((m) => m.LibraryComponent),
  },
  {
    path: 'u/:username/premieres',
    canActivate: [authGuard],
    loadComponent: () =>
      import('./features/premiere-history/premiere-history.component').then(
        (m) => m.PremiereHistoryComponent,
      ),
  },
  {
    path: 'admin',
    canActivate: [adminGuard],
    loadChildren: () => import('./features/admin/admin.routes').then((m) => m.ADMIN_ROUTES),
  },
  { path: '**', redirectTo: 'premiere' },
];
