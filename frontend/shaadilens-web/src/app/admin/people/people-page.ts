import { DatePipe } from '@angular/common';
import { Component, DestroyRef, computed, inject, resource, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { MatButtonModule } from '@angular/material/button';
import { MatFormFieldModule } from '@angular/material/form-field';
import { MatIconModule } from '@angular/material/icon';
import { MatInputModule } from '@angular/material/input';
import { MatProgressBarModule } from '@angular/material/progress-bar';
import { MatSelectModule } from '@angular/material/select';
import { AdminApi } from '../../core/api/admin-api';
import { Person } from '../../core/api/api.models';
import { Notify } from '../../core/notify';
import { faceCropStyle } from '../../shared/face-crop';
import { EventContext } from '../event/event-context';

/**
 * People: every face of the event grouped into "Person 1, Person 2…" by DBSCAN clustering (face-service).
 * Name a person, or link them to a guest — that guest then gets all of the person's photos, and the
 * person's faces help find their future photos.
 */
@Component({
  selector: 'app-people-page',
  imports: [DatePipe, FormsModule, MatButtonModule, MatFormFieldModule, MatIconModule, MatInputModule, MatProgressBarModule, MatSelectModule],
  template: `
    <header class="page-header">
      <div>
        <h2>People</h2>
        <p class="muted small">
          @if (people.value(); as p) {
            @if (p.updatedAt) {
              {{ p.people.length }} people found in {{ p.groupedFaces }} of {{ p.faces }} faces · updated {{ p.updatedAt | date: 'd MMM, HH:mm' }}.
              Faces seen fewer than 3 times aren’t grouped.
            } @else {
              Group all {{ p.faces }} faces into people — useful to spot who has no selfie yet, or to give a guest their photos.
            }
          }
        </p>
      </div>
      <button mat-flat-button (click)="run()" [disabled]="running()"><mat-icon>diversity_3</mat-icon>
        {{ people.value()?.updatedAt ? 'Update people' : 'Find people' }}</button>
    </header>
    @if (running() || (people.isLoading() && !people.hasValue())) {
      <mat-progress-bar mode="indeterminate" />
      @if (running()) { <p class="muted small">Grouping faces… this takes a few seconds to a minute.</p> }
    }

    <div class="grid">
      @for (p of people.value()?.people ?? []; track p.id) {
        <article class="panel person" [class.linked]="p.guestId">
          <div class="faces">
            @for (s of p.samples.slice(0, 4); track s.photoId) {
              <a class="face" [href]="s.webUrl" target="_blank" rel="noopener" [style]="crop(s)"></a>
            }
          </div>
          <div class="info">
            <mat-form-field appearance="outline" subscriptSizing="dynamic">
              <mat-label>Person {{ p.number }}</mat-label>
              <input matInput [ngModel]="p.name" (change)="rename(p, $any($event.target).value)" placeholder="Name (optional)" maxlength="100" />
            </mat-form-field>
            <p class="muted small">{{ p.photoCount }} photos · {{ p.faceCount }} faces</p>
            <mat-form-field appearance="outline" subscriptSizing="dynamic">
              <mat-label>Linked guest</mat-label>
              <mat-select [ngModel]="p.guestId" (ngModelChange)="link(p, $event)">
                <mat-option [value]="null">— not linked —</mat-option>
                @for (g of guests.value() ?? []; track g.id) {
                  <mat-option [value]="g.id">{{ g.name ?? 'Guest' }}{{ g.phone ? ' · ' + g.phone : '' }}</mat-option>
                }
              </mat-select>
            </mat-form-field>
          </div>
        </article>
      } @empty {
        @if (people.value()?.updatedAt) { <p class="muted">No one appears in 3 or more photos yet.</p> }
      }
    </div>
  `,
  styles: `
    .page-header { display: flex; justify-content: space-between; gap: 12px; align-items: flex-start; flex-wrap: wrap; }
    .page-header h2 { margin: 0; }
    .grid { display: grid; grid-template-columns: repeat(auto-fill, minmax(260px, 1fr)); gap: 12px; margin-top: 12px; }
    .person { display: flex; flex-direction: column; gap: 10px; margin: 0; }
    .person.linked { outline: 2px solid var(--mat-sys-primary); }
    .faces { display: grid; grid-template-columns: repeat(4, 1fr); gap: 4px; }
    .face { aspect-ratio: 1; border-radius: 8px; background-color: #222; display: block; }
    .info { display: flex; flex-direction: column; gap: 6px; }
    .info p { margin: 0; }
  `,
})
export class PeoplePage {
  private readonly ctx = inject(EventContext);
  private readonly api = inject(AdminApi);
  private readonly notify = inject(Notify);

  protected readonly people = resource({ params: () => this.ctx.eventId(), loader: ({ params: id }) => this.api.people(id) });
  protected readonly guests = resource({ params: () => this.ctx.eventId(), loader: ({ params: id }) => this.api.guests(id) });
  private readonly started = signal(false);
  protected readonly running = computed(() => this.started() || !!this.people.value()?.running);

  constructor() {
    // While the job runs, check every 3 s.
    const timer = setInterval(() => {
      if (this.running() && !this.people.isLoading()) this.people.reload();
      if (this.people.value() && !this.people.value()!.running) this.started.set(false);
    }, 3000);
    inject(DestroyRef).onDestroy(() => clearInterval(timer));
  }

  protected crop(s: Person['samples'][number]): Record<string, string> {
    return faceCropStyle(s.face, s.width, s.height, s.thumbUrl, s.webUrl);
  }

  protected async run(): Promise<void> {
    try {
      await this.api.findPeople(this.ctx.eventId()!);
      this.started.set(true);
      this.people.reload();
    } catch (err) {
      this.notify.error(err);
    }
  }

  protected async rename(p: Person, name: string): Promise<void> {
    await this.save(p, name.trim() || null, p.guestId);
  }

  protected async link(p: Person, guestId: string | null): Promise<void> {
    await this.save(p, p.name, guestId);
    if (guestId) this.notify.ok(`Linked. Their ${p.photoCount} photos are now in that guest’s gallery.`);
  }

  private async save(p: Person, name: string | null, guestId: string | null): Promise<void> {
    try {
      await this.api.updatePerson(p.id, name, guestId);
      this.people.reload();
      this.guests.reload();
    } catch (err) {
      this.notify.error(err);
    }
  }
}
