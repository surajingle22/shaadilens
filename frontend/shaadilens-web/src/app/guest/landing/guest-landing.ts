import { Component, input } from '@angular/core';
import { MatButtonModule } from '@angular/material/button';
import { MatIconModule } from '@angular/material/icon';

/**
 * Guest landing page (QR code target). Mobile-first.
 * `token` comes straight from the /g/:token route param (withComponentInputBinding).
 */
@Component({
  selector: 'app-guest-landing',
  imports: [MatButtonModule, MatIconModule],
  template: `
    <main class="landing">
      <div class="cover">
        <mat-icon class="cover-icon">favorite</mat-icon>
      </div>
      <h1>Welcome to the wedding gallery</h1>
      <p>Take one selfie and we'll find every photo you're in.</p>
      <button mat-flat-button disabled>
        <mat-icon>photo_camera</mat-icon> Find your photos
      </button>
      <p class="hint">Coming in Phase 1 · link <code>{{ token() }}</code></p>
    </main>
  `,
  styles: `
    .landing {
      min-height: 100dvh; display: flex; flex-direction: column; align-items: center;
      justify-content: center; gap: 12px; padding: 24px 16px; text-align: center;
    }
    .cover {
      width: 120px; height: 120px; border-radius: 50%; display: grid; place-items: center;
      background: var(--mat-sys-primary-container); color: var(--mat-sys-on-primary-container);
    }
    .cover-icon { font-size: 56px; width: 56px; height: 56px; }
    h1 { font: var(--mat-sys-headline-small); margin: 8px 0 0; }
    p { color: var(--mat-sys-on-surface-variant); max-width: 320px; margin: 0; }
    .hint { font: var(--mat-sys-body-small); }
  `,
})
export class GuestLanding {
  readonly token = input.required<string>();
}
