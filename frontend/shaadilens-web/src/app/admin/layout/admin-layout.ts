import { Component, inject } from '@angular/core';
import { MatButtonModule } from '@angular/material/button';
import { MatIconModule } from '@angular/material/icon';
import { MatMenuModule } from '@angular/material/menu';
import { MatProgressBarModule } from '@angular/material/progress-bar';
import { MatToolbarModule } from '@angular/material/toolbar';
import { Router, RouterLink, RouterLinkActive, RouterOutlet } from '@angular/router';
import { API } from '../../core/api/api-urls';
import { Auth } from '../../core/auth/auth';
import { UploadQueue } from '../upload/upload-queue';

@Component({
  selector: 'app-admin-layout',
  imports: [MatToolbarModule, MatButtonModule, MatIconModule, MatMenuModule, MatProgressBarModule, RouterOutlet, RouterLink, RouterLinkActive],
  template: `
    <mat-toolbar class="toolbar">
      <a routerLink="/admin/events" class="brand"><mat-icon>photo_camera</mat-icon> ShaadiLens</a>
      <span class="spacer"></span>
      <a mat-button routerLink="/admin/events" routerLinkActive="active">Events</a>
      <button mat-icon-button [matMenuTriggerFor]="menu" aria-label="More">
        <mat-icon>more_vert</mat-icon>
      </button>
      <mat-menu #menu="matMenu">
        <a mat-menu-item routerLink="/admin/system"><mat-icon>monitor_heart</mat-icon> System status</a>
        <a mat-menu-item [href]="hangfireUrl" target="_blank" rel="noopener"><mat-icon>work_history</mat-icon> Background jobs</a>
        <button mat-menu-item (click)="logout()"><mat-icon>logout</mat-icon> Sign out ({{ auth.session()?.email }})</button>
      </mat-menu>
    </mat-toolbar>

    <!-- Uploads continue while you browse; show a thin global progress bar. -->
    @if (uploads.busy()) {
      <mat-progress-bar mode="determinate" [value]="uploads.overall() * 100" class="upload-bar" />
    }

    <main class="content">
      <router-outlet />
    </main>
  `,
  styles: `
    .toolbar { gap: 4px; background: var(--mat-sys-primary-container); color: var(--mat-sys-on-primary-container);
      position: sticky; top: 0; z-index: 10; }
    .brand { display: flex; align-items: center; gap: 8px; font: var(--mat-sys-title-large); color: inherit; text-decoration: none; }
    .spacer { flex: 1; }
    .active { font-weight: 600; }
    .upload-bar { position: sticky; top: 64px; z-index: 10; }
    .content { padding: 16px; max-width: 1280px; margin: 0 auto; }
  `,
})
export class AdminLayout {
  protected readonly auth = inject(Auth);
  protected readonly uploads = inject(UploadQueue);
  private readonly router = inject(Router);
  protected readonly hangfireUrl = API.hangfire;

  protected async logout(): Promise<void> {
    if (this.uploads.busy() && !confirm('Uploads are still running. Sign out anyway?')) return;
    await this.auth.logout();
    await this.router.navigate(['/admin/login']);
  }
}
