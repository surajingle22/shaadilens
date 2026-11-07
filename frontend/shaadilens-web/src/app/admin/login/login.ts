import { Component, inject, input, signal } from '@angular/core';
import { FormBuilder, ReactiveFormsModule, Validators } from '@angular/forms';
import { MatButtonModule } from '@angular/material/button';
import { MatFormFieldModule } from '@angular/material/form-field';
import { MatIconModule } from '@angular/material/icon';
import { MatInputModule } from '@angular/material/input';
import { Router } from '@angular/router';
import { Auth } from '../../core/auth/auth';
import { errorMessage } from '../../core/notify';

@Component({
  selector: 'app-login',
  imports: [ReactiveFormsModule, MatFormFieldModule, MatInputModule, MatButtonModule, MatIconModule],
  template: `
    <main class="login">
      <form class="card" [formGroup]="form" (ngSubmit)="submit()">
        <mat-icon class="logo">photo_camera</mat-icon>
        <h1>ShaadiLens admin</h1>

        <mat-form-field appearance="outline">
          <mat-label>Email</mat-label>
          <input matInput type="email" formControlName="email" autocomplete="username" />
        </mat-form-field>

        <mat-form-field appearance="outline">
          <mat-label>Password</mat-label>
          <input matInput [type]="showPassword() ? 'text' : 'password'" formControlName="password" autocomplete="current-password" />
          <button mat-icon-button matSuffix type="button" (click)="showPassword.set(!showPassword())"
                  [attr.aria-label]="showPassword() ? 'Hide password' : 'Show password'">
            <mat-icon>{{ showPassword() ? 'visibility_off' : 'visibility' }}</mat-icon>
          </button>
        </mat-form-field>

        @if (error()) {
          <p class="error" role="alert">{{ error() }}</p>
        }

        <button mat-flat-button type="submit" [disabled]="form.invalid || busy()">
          {{ busy() ? 'Signing in…' : 'Sign in' }}
        </button>
        <p class="hint">Use ADMIN_EMAIL / ADMIN_PASSWORD from your .env file.</p>
      </form>
    </main>
  `,
  styles: `
    .login { min-height: 100dvh; display: grid; place-items: center; padding: 16px; }
    .card {
      width: min(380px, 100%); display: flex; flex-direction: column; gap: 4px; padding: 32px 24px;
      background: var(--mat-sys-surface-container-low); border-radius: var(--mat-sys-corner-extra-large);
    }
    .logo { font-size: 40px; width: 40px; height: 40px; color: var(--mat-sys-primary); align-self: center; }
    h1 { font: var(--mat-sys-headline-small); text-align: center; margin: 4px 0 20px; }
    .error { color: var(--mat-sys-error); margin: 0 0 12px; }
    .hint { font: var(--mat-sys-body-small); color: var(--mat-sys-on-surface-variant); text-align: center; margin-top: 12px; }
  `,
})
export class Login {
  private readonly auth = inject(Auth);
  private readonly router = inject(Router);

  /** From ?returnUrl= (set by the guard). */
  readonly returnUrl = input<string>();

  protected readonly busy = signal(false);
  protected readonly error = signal<string | null>(null);
  protected readonly showPassword = signal(false);

  protected readonly form = inject(FormBuilder).nonNullable.group({
    email: ['', [Validators.required, Validators.email]],
    password: ['', Validators.required],
  });

  protected async submit(): Promise<void> {
    this.busy.set(true);
    this.error.set(null);
    try {
      const { email, password } = this.form.getRawValue();
      await this.auth.login(email, password);
      await this.router.navigateByUrl(this.returnUrl() || '/admin/events');
    } catch (err) {
      this.error.set(errorMessage(err, 'Sign in failed'));
    } finally {
      this.busy.set(false);
    }
  }
}
