import {
  ApplicationConfig,
  provideAppInitializer,
  provideBrowserGlobalErrorListeners,
  provideZoneChangeDetection,
  inject,
} from '@angular/core';
import { provideRouter, withComponentInputBinding } from '@angular/router';
import { provideHttpClient, withInterceptors } from '@angular/common/http';
import { firstValueFrom } from 'rxjs';

import { routes } from './app.routes';
import { authInterceptor } from './core/auth.interceptor';
import { AnonymousSessionService } from './core/anonymous-session.service';
import { AuthService } from './core/auth.service';

export const appConfig: ApplicationConfig = {
  providers: [
    provideBrowserGlobalErrorListeners(),
    provideZoneChangeDetection({ eventCoalescing: true }),
    // Query params arrive as signal inputs on the routed component, so a screen's search term,
    // filter and page can live in the URL without an ActivatedRoute subscription.
    provideRouter(routes, withComponentInputBinding()),
    provideHttpClient(withInterceptors([authInterceptor])),
    // "Issue a lightweight, short-lived session token on first page load" (MARQUEE_PLAN.md,
    // Iteration 5). Only for visitors who are not signed in — a signed-in user already has an
    // identity, and giving them a second one would just be a way to clap twice.
    //
    // A stored session is checked first: it may have expired since the last visit, and permissions
    // may have changed. Checked before deciding about a visitor session, so a session the API no
    // longer accepts becomes a visitor who can clap, not a signed-in user who can do nothing.
    provideAppInitializer(async () => {
      const auth = inject(AuthService);
      const sessions = inject(AnonymousSessionService);
      if (auth.isLoggedIn()) {
        await firstValueFrom(auth.refreshUser());
      }
      if (!auth.isLoggedIn()) {
        await sessions.ensure();
      }
    }),
  ],
};
