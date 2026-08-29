import { Component, inject, signal } from '@angular/core';
import { ActivatedRoute, RouterLink } from '@angular/router';
import { AuthService } from '../../core/auth.service';
import { apiError } from '../../core/http-error';

type ConfirmStatus = 'checking' | 'confirmed' | 'failed';

/**
 * What the emailed confirm-email link opens (issue #47). Reads its token from the URL rather than a
 * route param — a query string is what a link built by AuthService.RegisterAsync can actually carry.
 */
@Component({
  selector: 'app-confirm-email',
  imports: [RouterLink],
  templateUrl: './confirm-email.component.html',
  styleUrl: './confirm-email.component.css',
})
export class ConfirmEmailComponent {
  private readonly route = inject(ActivatedRoute);
  private readonly auth = inject(AuthService);

  protected readonly status = signal<ConfirmStatus>('checking');
  protected readonly error = signal<string | null>(null);

  constructor() {
    const token = this.route.snapshot.queryParamMap.get('token');
    if (!token) {
      this.status.set('failed');
      this.error.set('This confirmation link is missing its token.');
      return;
    }

    this.auth.confirmEmail(token).subscribe({
      next: () => this.status.set('confirmed'),
      error: (err) => {
        this.status.set('failed');
        this.error.set(apiError(err, 'This confirmation link is invalid or has expired.'));
      },
    });
  }
}
