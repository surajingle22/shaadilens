import { CdkDrag, CdkDragDrop, CdkDragHandle, CdkDropList, moveItemInArray } from '@angular/cdk/drag-drop';
import { DecimalPipe } from '@angular/common';
import { Component, DestroyRef, computed, effect, inject, signal } from '@angular/core';
import { FormBuilder, FormsModule, ReactiveFormsModule, Validators } from '@angular/forms';
import { MatButtonModule } from '@angular/material/button';
import { MatCheckboxModule } from '@angular/material/checkbox';
import { MatFormFieldModule } from '@angular/material/form-field';
import { MatIconModule } from '@angular/material/icon';
import { MatInputModule } from '@angular/material/input';
import { MatProgressBarModule } from '@angular/material/progress-bar';
import { MatSlideToggleModule } from '@angular/material/slide-toggle';
import { MatTooltipModule } from '@angular/material/tooltip';
import { Router } from '@angular/router';
import { AdminApi } from '../../../core/api/admin-api';
import { Collection, EventDetail } from '../../../core/api/api.models';
import { Notify } from '../../../core/notify';
import { EventContext } from '../event-context';

@Component({
  selector: 'app-event-overview',
  imports: [
    DecimalPipe, FormsModule, ReactiveFormsModule, CdkDropList, CdkDrag, CdkDragHandle,
    MatButtonModule, MatCheckboxModule, MatFormFieldModule, MatIconModule, MatInputModule,
    MatProgressBarModule, MatSlideToggleModule, MatTooltipModule,
  ],
  templateUrl: './event-overview.html',
  styleUrl: './event-overview.scss',
})
export class EventOverview {
  protected readonly ctx = inject(EventContext);
  private readonly api = inject(AdminApi);
  private readonly notify = inject(Notify);
  private readonly router = inject(Router);
  private readonly fb = inject(FormBuilder).nonNullable;

  protected readonly stats = this.ctx.stats;
  protected readonly progress = computed(() => {
    const s = this.stats.value();
    return s && s.photos ? (s.processed + s.failed) / s.photos : 0;
  });

  protected readonly form = this.fb.group({
    name: ['', [Validators.required, Validators.maxLength(120)]],
    date: [''],
    venue: [''],
    themeColor: ['#b0004a'],
    faceSearchEnabled: [true],
    expiresAt: [''],
  });

  protected readonly faces = this.fb.group({
    matchThreshold: [0.42, [Validators.required, Validators.min(0.2), Validators.max(0.9)]],
    probableThreshold: [0.34, [Validators.required, Validators.min(0.2), Validators.max(0.9)]],
    minFaceSizePx: [40, [Validators.required, Validators.min(10)]],
    minDetScore: [0.6, [Validators.required, Validators.min(0.1), Validators.max(1)]],
    maxSelfieYaw: [30, [Validators.required, Validators.min(5), Validators.max(90)]],
  });

  protected readonly collections = signal<Collection[]>([]);
  protected readonly newCollection = signal('');
  protected readonly saving = signal(false);

  constructor() {
    // Fill the forms whenever the event (re)loads.
    effect(() => {
      const ev = this.ctx.event.value();
      if (!ev) return;
      this.form.reset({
        name: ev.name, date: ev.date ?? '', venue: ev.venue ?? '', themeColor: ev.themeColor,
        faceSearchEnabled: ev.faceSearchEnabled, expiresAt: ev.expiresAt?.slice(0, 10) ?? '',
      });
      this.faces.reset(ev.faceSettings);
      this.collections.set(ev.collections);
    });

    // While photos are processing, refresh the numbers every 3 s.
    const timer = setInterval(() => {
      const s = this.stats.value();
      if (s && s.pending > 0 && !this.stats.isLoading()) this.stats.reload();
    }, 3000);
    inject(DestroyRef).onDestroy(() => clearInterval(timer));
  }

  private get event(): EventDetail {
    return this.ctx.event.value()!;
  }

  protected async togglePublish(published: boolean): Promise<void> {
    await this.run(() => this.api.publish(this.event.id, published), published ? 'Published — guests can open links now' : 'Unpublished');
  }

  protected async saveDetails(): Promise<void> {
    const v = this.form.getRawValue();
    await this.run(
      () => this.api.updateEvent(this.event.id, {
        name: v.name, date: v.date || null, venue: v.venue || null, themeColor: v.themeColor, titleFont: this.event.titleFont,
        watermarkEnabled: this.event.watermarkEnabled, watermarkText: this.event.watermarkText,
        faceSearchEnabled: v.faceSearchEnabled, expiresAt: v.expiresAt ? new Date(v.expiresAt + 'T23:59:59').toISOString() : null,
      }),
      'Saved',
    );
  }

  protected async saveFaceSettings(): Promise<void> {
    await this.run(() => this.api.saveFaceSettings(this.event.id, this.faces.getRawValue()),
      'Saved. Guests get the new thresholds when they tap “Check for new photos”.');
  }

  protected async drop(e: CdkDragDrop<Collection[]>): Promise<void> {
    const list = [...this.collections()];
    moveItemInArray(list, e.previousIndex, e.currentIndex);
    this.collections.set(list);
    await this.run(() => this.api.reorderCollections(this.event.id, list.map((c) => c.id)));
  }

  protected async addCollection(): Promise<void> {
    const name = this.newCollection().trim();
    if (!name) return;
    await this.run(() => this.api.addCollection(this.event.id, name), `Added “${name}”`);
    this.newCollection.set('');
  }

  protected async renameCollection(c: Collection, name: string): Promise<void> {
    if (!name.trim() || name === c.name) return;
    await this.run(() => this.api.updateCollection(this.event.id, { ...c, name: name.trim() }));
  }

  protected async toggleHidden(c: Collection): Promise<void> {
    await this.run(() => this.api.updateCollection(this.event.id, { ...c, isHidden: !c.isHidden }),
      c.isHidden ? `“${c.name}” is visible to guests` : `“${c.name}” is hidden from guests`);
  }

  protected async deleteCollection(c: Collection): Promise<void> {
    if (!confirm(`Delete the empty collection “${c.name}”?`)) return;
    await this.run(() => this.api.deleteCollection(this.event.id, c.id), 'Deleted');
  }

  protected async retryFailed(): Promise<void> {
    const r = await this.api.retryFailed(this.event.id).catch((e) => (this.notify.error(e), null));
    if (r) this.notify.ok(`Re-queued ${r.queued} photo(s)`);
    this.stats.reload();
  }

  protected async deleteEvent(): Promise<void> {
    const name = prompt(`This deletes ALL photos, guests and selfies of “${this.event.name}”.\nType the event name to confirm:`);
    if (name !== this.event.name) return;
    await this.run(() => this.api.deleteEvent(this.event.id), 'Event deleted', false);
    await this.router.navigate(['/admin/events']);
  }

  /** Call the API, show a toast, reload the event. */
  private async run(action: () => Promise<unknown>, okMessage?: string, reload = true): Promise<void> {
    this.saving.set(true);
    try {
      await action();
      if (okMessage) this.notify.ok(okMessage);
      if (reload) this.ctx.reload();
    } catch (err) {
      this.notify.error(err);
      if (reload) this.ctx.reload();
    } finally {
      this.saving.set(false);
    }
  }
}
