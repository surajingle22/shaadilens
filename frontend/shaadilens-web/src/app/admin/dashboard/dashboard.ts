import { Component } from '@angular/core';
import { ApiStatus } from '../../shared/api-status/api-status';

@Component({
  selector: 'app-dashboard',
  imports: [ApiStatus],
  template: `
    <h1>Dashboard</h1>
    <p class="hint">Events, uploads and processing progress arrive in Phase 1.</p>
    <app-api-status />
  `,
  styles: `
    h1 { font: var(--mat-sys-headline-small); }
    .hint { color: var(--mat-sys-on-surface-variant); }
  `,
})
export class Dashboard {}
