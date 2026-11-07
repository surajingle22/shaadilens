import { Component, inject, input, output, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { MatButtonModule } from '@angular/material/button';
import { MatIconModule } from '@angular/material/icon';
import { GuestApi } from '../../core/api/guest-api';
import { errorMessage } from '../../core/notify';

/** 6-digit PIN. Uses a single numeric input so phone keyboards show digits and paste/autofill work. */
@Component({
  selector: 'app-pin-step',
  imports: [FormsModule, MatButtonModule, MatIconModule],
  template: `
    <main class="step">
      <mat-icon class="icon">lock</mat-icon>
      <h1>Enter the PIN</h1>
      <p class="muted">You’ll find it on the card or poster next to the QR code.</p>
      <form (ngSubmit)="submit()">
        <input class="pin" [ngModel]="pin()" (ngModelChange)="onPin($event)" name="pin"
               inputmode="numeric" autocomplete="one-time-code" maxlength="6" placeholder="••••••" aria-label="6-digit PIN" autofocus />
        @if (error()) { <p class="error" role="alert">{{ error() }}</p> }
        <button mat-flat-button type="submit" class="cta" [disabled]="pin().length !== 6 || busy()">{{ busy() ? 'Checking…' : 'Continue' }}</button>
      </form>
    </main>
  `,
  styles: `
    .step { min-height: 100dvh; display: flex; flex-direction: column; align-items: center; justify-content: center; gap: 8px; padding: 24px; text-align: center; }
    .icon { font-size: 40px; width: 40px; height: 40px; color: var(--mat-sys-primary); }
    h1 { font: var(--mat-sys-headline-small); margin: 0; }
    form { display: flex; flex-direction: column; align-items: center; gap: 12px; margin-top: 12px; }
    .pin { width: 220px; font-size: 32px; letter-spacing: 12px; text-align: center; padding: 10px; border-radius: 12px;
      border: 2px solid var(--mat-sys-outline-variant); background: var(--mat-sys-surface-container-lowest); color: inherit; }
    .pin:focus { outline: none; border-color: var(--mat-sys-primary); }
    .cta { height: 48px; min-width: 200px; border-radius: 24px; }
    .error { color: var(--mat-sys-error); margin: 0; max-width: 300px; }
    .muted { color: var(--mat-sys-on-surface-variant); margin: 0; }
  `,
})
export class PinStep {
  private readonly api = inject(GuestApi);
  readonly token = input.required<string>();
  readonly verified = output<string>();

  protected readonly pin = signal('');
  protected readonly busy = signal(false);
  protected readonly error = signal<string | null>(null);

  /** Keep digits only, max 6. */
  protected onPin(value: string): void {
    this.pin.set(value.replace(/\D/g, '').slice(0, 6));
  }

  protected async submit(): Promise<void> {
    this.busy.set(true);
    this.error.set(null);
    try {
      const { pinTicket } = await this.api.verifyPin(this.token(), this.pin());
      this.verified.emit(pinTicket);
    } catch (err) {
      this.error.set(errorMessage(err, 'That PIN isn’t right.'));
      this.pin.set('');
    } finally {
      this.busy.set(false);
    }
  }
}
