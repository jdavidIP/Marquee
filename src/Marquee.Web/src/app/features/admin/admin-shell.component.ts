import { Component, inject } from '@angular/core';
import { RouterLink, RouterLinkActive, RouterOutlet } from '@angular/router';
import { AuthService } from '../../core/auth.service';

/**
 * The operations area's frame: heading, tab navigation, and the outlet its screens render into.
 *
 * Tabs are routes rather than a switched signal, which buys three things. The overview's 3-second
 * poll stops when you navigate away, because leaving destroys the component. Search terms, filters
 * and page numbers live in the URL, so a refresh or the back button behaves. And each tab carries
 * its own permission guard, so a role holding only some of them lands on the part it can use.
 */
@Component({
  selector: 'app-admin-shell',
  standalone: true,
  imports: [RouterOutlet, RouterLink, RouterLinkActive],
  templateUrl: './admin-shell.component.html',
  styleUrl: './admin-shell.component.css',
})
export class AdminShellComponent {
  protected readonly auth = inject(AuthService);
}
