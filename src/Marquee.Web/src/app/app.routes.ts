import { Routes } from '@angular/router';
import { authGuard } from './core/auth.guard';

export const routes: Routes = [
  { path: '', pathMatch: 'full', redirectTo: 'premiere' },
  {
    path: 'login',
    loadComponent: () => import('./features/login/login.component').then((m) => m.LoginComponent),
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
  { path: '**', redirectTo: 'premiere' },
];
