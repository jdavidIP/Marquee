import { Routes } from '@angular/router';
import { requirePermission } from '../../core/admin.guard';
import { Permissions } from '../../core/auth.service';
import { AdminShellComponent } from './admin-shell.component';

/**
 * The operations area. The shell is loaded eagerly with this chunk — it is a heading and a tab bar —
 * while each screen stays its own lazy chunk, so opening Users does not also fetch the Premieres
 * editors.
 */
export const ADMIN_ROUTES: Routes = [
  {
    path: '',
    component: AdminShellComponent,
    children: [
      { path: '', pathMatch: 'full', redirectTo: 'overview' },
      {
        path: 'overview',
        canActivate: [requirePermission(Permissions.ViewUsers)],
        loadComponent: () =>
          import('./admin-dashboard.component').then((m) => m.AdminDashboardComponent),
      },
      {
        path: 'users',
        canActivate: [requirePermission(Permissions.ViewUsers)],
        loadComponent: () => import('./admin-users.component').then((m) => m.AdminUsersComponent),
      },
      // An unknown operations path lands on the overview rather than bouncing the admin out of the
      // area entirely, which the app-level wildcard would do.
      { path: '**', redirectTo: 'overview' },
    ],
  },
];
