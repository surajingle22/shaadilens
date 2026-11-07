import { Component } from '@angular/core';
import { MatButtonModule } from '@angular/material/button';
import { MatIconModule } from '@angular/material/icon';
import { MatToolbarModule } from '@angular/material/toolbar';
import { RouterLink, RouterLinkActive, RouterOutlet } from '@angular/router';

@Component({
  selector: 'app-admin-layout',
  imports: [MatToolbarModule, MatButtonModule, MatIconModule, RouterOutlet, RouterLink, RouterLinkActive],
  template: `
    <mat-toolbar class="toolbar">
      <mat-icon>photo_camera</mat-icon>
      <span class="brand">ShaadiLens</span>
      <span class="spacer"></span>
      <a mat-button routerLink="dashboard" routerLinkActive="active">Dashboard</a>
    </mat-toolbar>
    <main class="content">
      <router-outlet />
    </main>
  `,
  styles: `
    .toolbar { gap: 8px; background: var(--mat-sys-primary-container); color: var(--mat-sys-on-primary-container); }
    .brand { font: var(--mat-sys-title-large); }
    .spacer { flex: 1; }
    .active { font-weight: 600; }
    .content { padding: 16px; max-width: 1200px; margin: 0 auto; }
  `,
})
export class AdminLayout {}
