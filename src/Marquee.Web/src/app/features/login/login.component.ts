import { Component, inject, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { Router } from '@angular/router';
import { AuthService } from '../../core/auth.service';
import { apiError } from '../../core/http-error';

@Component({
  selector: 'app-login',
  imports: [FormsModule],
  templateUrl: './login.component.html',
  styleUrl: './login.component.css',
})
export class LoginComponent {
  private readonly auth = inject(AuthService);
  private readonly router = inject(Router);

  protected readonly mode = signal<'login' | 'register'>('login');
  protected readonly busy = signal(false);
  protected readonly error = signal<string | null>(null);

  protected username = '';
  protected email = '';
  protected password = '';

  toggle(): void {
    this.mode.set(this.mode() === 'login' ? 'register' : 'login');
    this.error.set(null);
  }

  submit(): void {
    this.error.set(null);
    this.busy.set(true);

    const done = {
      next: () => this.router.navigate(['/premiere']),
      error: (err: unknown) => {
        this.busy.set(false);
        this.error.set(apiError(err, 'Something went wrong. Please try again.'));
      },
    };

    if (this.mode() === 'login') {
      this.auth.login(this.username.trim(), this.password).subscribe(done);
    } else {
      this.auth.register(this.username.trim(), this.email.trim(), this.password).subscribe(done);
    }
  }
}
