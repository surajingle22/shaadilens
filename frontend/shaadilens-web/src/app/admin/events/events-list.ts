import { DatePipe } from '@angular/common';
import { Component, inject, resource, signal } from '@angular/core';
import { FormBuilder, ReactiveFormsModule, Validators } from '@angular/forms';
import { MatButtonModule } from '@angular/material/button';
import { MatChipsModule } from '@angular/material/chips';
import { MatFormFieldModule } from '@angular/material/form-field';
import { MatIconModule } from '@angular/material/icon';
import { MatInputModule } from '@angular/material/input';
import { MatProgressBarModule } from '@angular/material/progress-bar';
import { Router, RouterLink } from '@angular/router';
import { AdminApi } from '../../core/api/admin-api';
import { Notify } from '../../core/notify';

@Component({
  selector: 'app-events-list',
  imports: [DatePipe, ReactiveFormsModule, RouterLink, MatButtonModule, MatChipsModule, MatFormFieldModule, MatIconModule, MatInputModule, MatProgressBarModule],
  template: `
    <header class="page-header">
      <h1>Events</h1>
      <button mat-flat-button (click)="creating.set(!creating())"><mat-icon>add</mat-icon> New event</button>
    </header>

    @if (creating()) {
      <form class="panel create" [formGroup]="form" (ngSubmit)="create()">
        <mat-form-field appearance="outline" class="grow">
          <mat-label>Name</mat-label>
          <input matInput formControlName="name" placeholder="Suraj & Priya's Wedding" />
        </mat-form-field>
        <mat-form-field appearance="outline">
          <mat-label>Date</mat-label>
          <input matInput type="date" formControlName="date" />
        </mat-form-field>
        <mat-form-field appearance="outline">
          <mat-label>Venue</mat-label>
          <input matInput formControlName="venue" />
        </mat-form-field>
        <button mat-flat-button type="submit" [disabled]="form.invalid || busy()">Create</button>
      </form>
    }

    @if (events.isLoading()) {
      <mat-progress-bar mode="indeterminate" />
    }

    <div class="card-grid">
      @for (ev of events.value() ?? []; track ev.id) {
        <a class="panel event-card" [routerLink]="['/admin/events', ev.id]">
          <h2>{{ ev.name }}</h2>
          <p class="muted">
            {{ ev.date ? (ev.date | date: 'd MMM y') : 'No date' }}@if (ev.venue) { · {{ ev.venue }} }
          </p>
          <div class="facts">
            <span><mat-icon>photo_library</mat-icon> {{ ev.photoCount }}</span>
            <span><mat-icon>group</mat-icon> {{ ev.guestCount }}</span>
            <mat-chip-set>
              <mat-chip [highlighted]="ev.isPublished">{{ ev.isPublished ? 'Published' : 'Draft' }}</mat-chip>
            </mat-chip-set>
          </div>
        </a>
      } @empty {
        @if (!events.isLoading()) {
          <p class="muted">No events yet. Create one to start uploading photos.</p>
        }
      }
    </div>
  `,
  styles: `
    .create { display: flex; flex-wrap: wrap; gap: 0 12px; align-items: baseline; margin-bottom: 16px; }
    .grow { flex: 1 1 260px; }
    .event-card { color: inherit; text-decoration: none; display: block; transition: box-shadow .15s; }
    .event-card:hover { box-shadow: var(--mat-sys-level2); }
    .event-card h2 { font: var(--mat-sys-title-large); margin: 0 0 4px; }
    .facts { display: flex; align-items: center; gap: 16px; margin-top: 12px; }
    .facts span { display: inline-flex; align-items: center; gap: 4px; }
  `,
})
export class EventsList {
  private readonly api = inject(AdminApi);
  private readonly router = inject(Router);
  private readonly notify = inject(Notify);

  protected readonly events = resource({ loader: () => this.api.listEvents() });
  protected readonly creating = signal(false);
  protected readonly busy = signal(false);
  protected readonly form = inject(FormBuilder).nonNullable.group({
    name: ['', [Validators.required, Validators.maxLength(120)]],
    date: [''],
    venue: [''],
  });

  protected async create(): Promise<void> {
    this.busy.set(true);
    try {
      const v = this.form.getRawValue();
      const ev = await this.api.createEvent({
        name: v.name, date: v.date || null, venue: v.venue || null, themeColor: null, titleFont: null,
        watermarkEnabled: false, watermarkText: null, faceSearchEnabled: true, expiresAt: null,
      });
      this.notify.ok('Event created with Haldi, Mehendi, Sangeet, Wedding and Reception');
      await this.router.navigate(['/admin/events', ev.id, 'upload']);
    } catch (err) {
      this.notify.error(err);
    } finally {
      this.busy.set(false);
    }
  }
}
