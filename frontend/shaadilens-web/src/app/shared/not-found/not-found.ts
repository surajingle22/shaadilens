import { Component } from '@angular/core';

@Component({
  selector: 'app-not-found',
  template: `
    <main class="not-found">
      <h1>Page not found</h1>
      <p>If you scanned a QR code, please scan it again or ask the couple for a fresh link.</p>
    </main>
  `,
  styles: `
    .not-found { padding: 48px 16px; text-align: center; }
  `,
})
export class NotFound {}
