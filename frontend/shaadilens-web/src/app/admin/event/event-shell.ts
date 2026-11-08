import { Component, effect, inject, input } from '@angular/core';
import { MatChipsModule } from '@angular/material/chips';
import { MatIconModule } from '@angular/material/icon';
import { MatProgressBarModule } from '@angular/material/progress-bar';
import { MatTabsModule } from '@angular/material/tabs';
import { RouterLink, RouterLinkActive, RouterOutlet } from '@angular/router';
import { Realtime } from '../../core/realtime/realtime';
import { EventContext } from './event-context';

/** Event header + tab navigation. Each tab is its own lazy-loaded child route. */
@Component({
  selector: 'app-event-shell',
  imports: [MatTabsModule, MatIconModule, MatChipsModule, MatProgressBarModule, RouterOutlet, RouterLink, RouterLinkActive],
  providers: [EventContext, Realtime],
  template: `
    @if (ctx.event.hasValue()) {
      <header class="page-header">
        <div>
          <a routerLink="/admin/events" class="back"><mat-icon>arrow_back</mat-icon> Events</a>
          <h1>{{ ctx.event.value().name }}</h1>
        </div>
        <mat-chip-set>
          <mat-chip [highlighted]="ctx.event.value().isPublished">{{ ctx.event.value().isPublished ? 'Published' : 'Draft — guests can’t see it yet' }}</mat-chip>
        </mat-chip-set>
      </header>
    } @else if (ctx.event.isLoading()) {
      <mat-progress-bar mode="indeterminate" />
    } @else if (ctx.event.error()) {
      <p class="muted">This event couldn’t be loaded. <a routerLink="/admin/events">Back to events</a></p>
    }

    <nav mat-tab-nav-bar [tabPanel]="panel" class="tabs">
      @for (t of tabs; track t.path) {
        <a mat-tab-link [routerLink]="t.path" routerLinkActive #rla="routerLinkActive" [active]="rla.isActive">
          <mat-icon>{{ t.icon }}</mat-icon>&nbsp;{{ t.label }}
        </a>
      }
    </nav>
    <mat-tab-nav-panel #panel>
      <div class="tab-body"><router-outlet /></div>
    </mat-tab-nav-panel>
  `,
  styles: `
    .back { display: inline-flex; align-items: center; gap: 4px; color: var(--mat-sys-on-surface-variant); text-decoration: none; font: var(--mat-sys-label-large); }
    .tabs { margin-bottom: 16px; }
    .tab-body { padding-top: 4px; }
  `,
})
export class EventShell {
  protected readonly ctx = inject(EventContext);

  /** From the route (:eventId). */
  readonly eventId = input.required<string>();

  protected readonly tabs = [
    { path: 'overview', label: 'Overview', icon: 'dashboard' },
    { path: 'upload', label: 'Upload', icon: 'cloud_upload' },
    { path: 'photos', label: 'Photos', icon: 'photo_library' },
    { path: 'share', label: 'Share', icon: 'qr_code_2' },
    { path: 'guests', label: 'Guests', icon: 'group' },
    { path: 'people', label: 'People', icon: 'diversity_3' },
    { path: 'selections', label: 'Selections', icon: 'favorite' },
  ];

  constructor() {
    effect(() => this.ctx.eventId.set(this.eventId()));
  }
}
