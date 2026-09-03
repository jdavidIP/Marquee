import { Component, inject } from '@angular/core';
import { RouterOutlet, RouterLink, RouterLinkActive, Router } from '@angular/router';
import { AuthService } from './core/auth.service';

@Component({
  selector: 'app-root',
  imports: [RouterOutlet, RouterLink, RouterLinkActive],
  templateUrl: './app.html',
  styleUrl: './app.css',
})
export class App {
  protected readonly auth = inject(AuthService);
  private readonly router = inject(Router);

  /**
   * The two letters that stand in for a face. Usernames here are a single token, so this is the
   * first two characters rather than initials of separate words — "yourname" reads as YO.
   */
  protected initials(username: string): string {
    return username.slice(0, 2).toUpperCase();
  }

  logout(): void {
    this.auth.logout();
    this.router.navigate(['/login']);
  }
}
