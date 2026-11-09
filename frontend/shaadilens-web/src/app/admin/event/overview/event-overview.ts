import { CdkDrag, CdkDragDrop, CdkDragHandle, CdkDropList, moveItemInArray } from '@angular/cdk/drag-drop';
import { DatePipe, DecimalPipe } from '@angular/common';
import { Component, DestroyRef, computed, effect, inject, signal } from '@angular/core';
import { toSignal } from '@angular/core/rxjs-interop';
import { FormBuilder, FormsModule, ReactiveFormsModule, Validators } from '@angular/forms';
import { MatButtonModule } from '@angular/material/button';
import { MatCheckboxModule } from '@angular/material/checkbox';
import { MatFormFieldModule } from '@angular/material/form-field';
import { MatIconModule } from '@angular/material/icon';
import { MatInputModule } from '@angular/material/input';
import { MatProgressBarModule } from '@angular/material/progress-bar';
import { MatSelectModule } from '@angular/material/select';
import { MatSlideToggleModule } from '@angular/material/slide-toggle';
import { MatTooltipModule } from '@angular/material/tooltip';
import { Router } from '@angular/router';
import { AdminApi } from '../../../core/api/admin-api';
import { Collection, EventDetail, ThresholdPreview } from '../../../core/api/api.models';
import { Notify } from '../../../core/notify';
import { EventContext } from '../event-context';

@Component({
  selector: 'app-event-overview',
  imports: [
    DatePipe, DecimalPipe, FormsModule, ReactiveFormsModule, MatSelectModule, CdkDropList, CdkDrag, CdkDragHandle,
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
    titleFont: [null as string | null],
    watermarkEnabled: [false],
    watermarkText: [''],
  });

  /** Same list the API accepts (Event.TitleFonts); loaded from Google Fonts on the guest pages. */
  protected readonly fonts = ['Playfair Display', 'Cormorant Garamond', 'Great Vibes', 'Dancing Script', 'Marcellus', 'Poppins'];

  protected isPast(iso: string): boolean {
    return new Date(iso).getTime() < Date.now();
  }

  protected readonly faces = this.fb.group({
    matchThreshold: [0.42, [Validators.required, Validators.min(0.2), Validators.max(0.9)]],
    probableThreshold: [0.34, [Validators.required, Validators.min(0.2), Validators.max(0.9)]],
    minFaceSizePx: [40, [Validators.required, Validators.min(10)]],
    minDetScore: [0.6, [Validators.required, Validators.min(0.1), Validators.max(1)]],
    maxSelfieYaw: [30, [Validators.required, Validators.min(5), Validators.max(90)]],
  });

  protected readonly reels = this.fb.group({
    enabled: [true],
    photoCount: [12, [Validators.required, Validators.min(3), Validators.max(30)]],
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
        titleFont: ev.titleFont, watermarkEnabled: ev.watermarkEnabled, watermarkText: ev.watermarkText ?? '',
      });
      this.faces.reset(ev.faceSettings);
      this.reels.reset(ev.reels);
      this.collections.set(ev.collections);
    });

    // Live updates come over SignalR (EventContext). If that connection is down, poll every 3 s instead.
    const timer = setInterval(() => {
      const s = this.stats.value();
      if (!this.ctx.live() && s && s.pending > 0 && !this.stats.isLoading()) this.stats.reload();
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
    const before = this.event;
    const watermarkChanged = v.watermarkEnabled !== before.watermarkEnabled ||
      (v.watermarkEnabled && v.watermarkText.trim() !== (before.watermarkText ?? ''));
    await this.run(
      () => this.api.updateEvent(this.event.id, {
        name: v.name, date: v.date || null, venue: v.venue || null, themeColor: v.themeColor, titleFont: v.titleFont,
        watermarkEnabled: v.watermarkEnabled, watermarkText: v.watermarkText.trim() || null,
        faceSearchEnabled: v.faceSearchEnabled, expiresAt: v.expiresAt ? new Date(v.expiresAt + 'T23:59:59').toISOString() : null,
      }),
      'Saved',
    );
    // New uploads get the new watermark automatically; existing photos need their web image re-rendered.
    if (watermarkChanged && (this.stats.value()?.photos ?? 0) > 0 &&
        confirm('Apply the watermark change to the photos already uploaded? (Runs in the background.)')) {
      await this.run(async () => {
        const r = await this.api.rebuildImages(this.event.id);
        this.notify.ok(`Updating ${r.queued} photo(s) in the background…`);
      }, undefined, false);
    }
  }

  protected async saveHighlights(forAllGuests: boolean): Promise<void> {
    await this.run(() => this.api.saveHighlightSettings(this.event.id, forAllGuests), 'Saved');
  }

  protected async saveReelSettings(): Promise<void> {
    await this.run(() => this.api.saveReelSettings(this.event.id, this.reels.getRawValue()), 'Reel settings saved');
  }

  protected async saveFaceSettings(): Promise<void> {
    await this.run(() => this.api.saveFaceSettings(this.event.id, this.faces.getRawValue()),
      'Saved. Click “Apply to all guests” to update everyone now (new uploads use them automatically).');
  }

  // ---------- threshold preview ----------
  protected readonly preview = signal<ThresholdPreview | null>(null);
  protected readonly previewing = signal(false);
  /** Current (unsaved) slider/field values, as signals so the preview updates while typing. */
  private readonly faceValues = toSignal(this.faces.valueChanges, { initialValue: this.faces.getRawValue() });
  protected readonly matchT = computed(() => Number(this.faceValues().matchThreshold ?? 0.42));
  protected readonly probableT = computed(() => Number(this.faceValues().probableThreshold ?? 0.34));

  /** (guest, photo) pairs with best similarity ≥ t (Confirmed/Rejected excluded — thresholds don't move them). */
  protected countAt(t: number): number {
    const from = Math.round(t * 100);
    return (this.preview()?.buckets ?? []).filter((b) => Math.round(b.score * 100) >= from).reduce((s, b) => s + b.count, 0);
  }

  /** One bar per 0.01 bucket from MinScore to 1.00, height relative to the tallest. */
  protected readonly bars = computed(() => {
    const p = this.preview();
    if (!p) return [];
    const byScore = new Map(p.buckets.map((b) => [Math.round(b.score * 100), b.count]));
    const max = Math.max(1, ...p.buckets.map((b) => b.count));
    const bars = [];
    for (let s = Math.round(p.minScore * 100); s <= 100; s++) {
      const count = byScore.get(s) ?? 0;
      const score = s / 100;
      const zone = score >= this.matchT() - 1e-6 ? 'match' : score >= this.probableT() - 1e-6 ? 'maybe' : 'below';
      bars.push({ score, count, zone, height: count ? Math.max(4, (count / max) * 100) : 0 });
    }
    return bars;
  });

  /** Position (0–100 %) of a threshold marker on the histogram's x-axis. */
  protected markerLeft(t: number): number {
    const min = this.preview()?.minScore ?? 0.2;
    const steps = Math.round((1 - min) * 100) + 1;
    // Values below the chart floor (0.20) are pinned to the left edge.
    return Math.max(0, ((Math.round(t * 100) - Math.round(min * 100)) / steps) * 100);
  }

  protected async loadPreview(): Promise<void> {
    this.previewing.set(true);
    try {
      this.preview.set(await this.api.thresholdPreview(this.event.id));
    } catch (err) {
      this.notify.error(err);
    } finally {
      this.previewing.set(false);
    }
  }

  protected async rematchAll(): Promise<void> {
    await this.run(() => this.api.rematchAll(this.event.id), 'Re-matching every guest in the background (usually under a minute).', false);
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
