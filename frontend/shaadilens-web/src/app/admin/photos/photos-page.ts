import { Component, DestroyRef, ElementRef, afterNextRender, computed, effect, inject, signal, untracked, viewChild } from '@angular/core';
import { NgTemplateOutlet } from '@angular/common';
import { FormsModule } from '@angular/forms';
import { MatButtonModule } from '@angular/material/button';
import { MatButtonToggleModule } from '@angular/material/button-toggle';
import { MatChipsModule } from '@angular/material/chips';
import { MatDialog } from '@angular/material/dialog';
import { MatFormFieldModule } from '@angular/material/form-field';
import { MatIconModule } from '@angular/material/icon';
import { MatProgressBarModule } from '@angular/material/progress-bar';
import { MatSelectModule } from '@angular/material/select';
import { MatTooltipModule } from '@angular/material/tooltip';
import { AdminApi } from '../../core/api/admin-api';
import { CullingSummary, Photo, PhotoFilter, PhotoStatus } from '../../core/api/api.models';
import { Notify } from '../../core/notify';
import { EventContext } from '../event/event-context';
import { PhotoDialog, PhotoDialogData } from './photo-dialog';
import { SuggestHighlightsDialog } from './suggest-highlights-dialog';
import { groupBursts } from './bursts';

const PAGE_SIZE = 60;

@Component({
  selector: 'app-photos-page',
  imports: [NgTemplateOutlet, FormsModule, MatButtonModule, MatButtonToggleModule, MatChipsModule, MatFormFieldModule, MatIconModule, MatProgressBarModule, MatSelectModule, MatTooltipModule],
  template: `
    <div class="filters">
      <mat-form-field appearance="outline">
        <mat-label>Collection</mat-label>
        <mat-select [ngModel]="collectionId()" (ngModelChange)="collectionId.set($event)">
          <mat-option [value]="null">All collections</mat-option>
          @for (c of ctx.event.value()?.collections ?? []; track c.id) {
            <mat-option [value]="c.id">{{ c.name }} ({{ c.photoCount }})</mat-option>
          }
        </mat-select>
      </mat-form-field>
      <mat-button-toggle-group [ngModel]="status()" (ngModelChange)="status.set($event)" aria-label="Status filter">
        <mat-button-toggle [value]="null">All</mat-button-toggle>
        <mat-button-toggle value="FacesDetected">Done</mat-button-toggle>
        <mat-button-toggle value="Resized">Detecting faces</mat-button-toggle>
        <mat-button-toggle value="Failed">Failed</mat-button-toggle>
      </mat-button-toggle-group>
      <span class="muted">{{ total() }} photo(s)</span>
    </div>

    <!-- Culling report: AI hints for what the photographer can skip. -->
    <div class="culling">
      <mat-chip-listbox [ngModel]="filter()" (ngModelChange)="filter.set($event ?? 'All')" aria-label="Culling filter">
        <mat-chip-option value="All">All</mat-chip-option>
        <mat-chip-option value="Blurry" matTooltip="The sharpest face (or, without people, the whole photo) is soft">
          <mat-icon matChipAvatar>blur_on</mat-icon> Blurry · {{ summary()?.blurry ?? '…' }}</mat-chip-option>
        <mat-chip-option value="EyesClosed" matTooltip="Someone in front has their eyes closed">
          <mat-icon matChipAvatar>visibility_off</mat-icon> Eyes closed · {{ summary()?.eyesClosed ?? '…' }}</mat-chip-option>
        <mat-chip-option value="Duplicates" matTooltip="Burst shots of the same moment — keep the best one">
          <mat-icon matChipAvatar>burst_mode</mat-icon> Duplicates · {{ summary()?.burstExtras ?? '…' }}</mat-chip-option>
        <mat-chip-option value="Highlights"><mat-icon matChipAvatar>star</mat-icon> Highlights · {{ summary()?.highlights ?? '…' }}</mat-chip-option>
        <mat-chip-option value="Hidden"><mat-icon matChipAvatar>hide_image</mat-icon> Hidden · {{ summary()?.hidden ?? '…' }}</mat-chip-option>
      </mat-chip-listbox>
      @if (filter() === 'Duplicates' && summary()?.burstExtras) {
        <button mat-flat-button (click)="hideExtras()"><mat-icon>auto_fix_high</mat-icon> Keep best, hide the rest</button>
      }
      @if (filter() === 'Highlights') {
        <button mat-flat-button (click)="suggestHighlights()"><mat-icon>auto_awesome</mat-icon> Suggest highlights</button>
      }
    </div>
    @if (summary()?.notAnalysed; as n) {
      <p class="panel info">
        <mat-icon>auto_awesome</mat-icon>
        {{ n }} photo(s) were processed before the culling report existed.
        <button mat-button (click)="analyzeExisting()">Analyze them</button>
      </p>
    }

    @if (loading() && !photos().length) {
      <mat-progress-bar mode="indeterminate" />
    }

    <ng-template #tile let-p>
      <button class="tile" (click)="open(p)" [class.hidden]="p.isHidden" [class.keeper]="p.isBurstBest && filter() === 'Duplicates'" [attr.aria-label]="p.fileName">
        @if (p.thumbUrl) {
          <img [src]="p.thumbUrl" [alt]="p.fileName" loading="lazy" />
        } @else {
          <div class="placeholder"><mat-icon>hourglass_top</mat-icon></div>
        }
        <span class="badges">
          @if (p.status === 'Failed') { <mat-icon class="bad" [matTooltip]="p.error ?? 'Failed'">error</mat-icon> }
          @else if (p.status !== 'FacesDetected' && p.status !== 'Matched') { <mat-icon matTooltip="Processing">autorenew</mat-icon> }
          @else { <span class="faces" matTooltip="Faces found"><mat-icon>face</mat-icon>{{ p.faceCount }}</span> }
          @if (p.isHighlight) { <mat-icon matTooltip="Highlight">star</mat-icon> }
          @if (p.isHidden) { <mat-icon matTooltip="Hidden from guests">visibility_off</mat-icon> }
          @if (p.isBlurry) { <mat-icon matTooltip="Blurry">blur_on</mat-icon> }
          @if (p.closedEyes) { <mat-icon matTooltip="Eyes closed">bedtime</mat-icon> }
          @if (p.burstGroupId) { <mat-icon [matTooltip]="p.isBurstBest ? 'Best of its burst' : 'Near-duplicate'">{{ p.isBurstBest ? 'verified' : 'burst_mode' }}</mat-icon> }
        </span>
      </button>
    </ng-template>

    @if (filter() === 'Duplicates') {
      <!-- One row per burst, keeper first. -->
      @for (b of bursts(); track b.id) {
        <section class="burst">
          @for (p of b.photos; track p.id) {
            <div class="burst-item">
              <ng-container *ngTemplateOutlet="tile; context: { $implicit: p }" />
              @if (p.isBurstBest) { <span class="keep-label"><mat-icon>verified</mat-icon> Keeper</span> }
              @else { <button mat-button class="keep-label" (click)="keep(p)">Keep this one</button> }
            </div>
          }
        </section>
      } @empty {
        @if (!loading()) { <p class="muted">No near-duplicates found.</p> }
      }
    } @else {
      <div class="grid">
        @for (p of photos(); track p.id) {
          <ng-container *ngTemplateOutlet="tile; context: { $implicit: p }" />
        } @empty {
          @if (!loading()) { <p class="muted">No photos match these filters.</p> }
        }
      </div>
    }

    <!-- Infinite scroll: when this comes into view, load the next page. -->
    <div #sentinel class="sentinel">
      @if (loading() && photos().length) { <mat-progress-bar mode="indeterminate" /> }
    </div>
  `,
  styles: `
    .filters { display: flex; flex-wrap: wrap; gap: 12px; align-items: center; margin-bottom: 8px; }
    .filters mat-form-field { width: 240px; }
    .grid { display: grid; grid-template-columns: repeat(auto-fill, minmax(150px, 1fr)); gap: 6px; }
    .tile { position: relative; aspect-ratio: 1; padding: 0; border: none; border-radius: 6px; overflow: hidden; cursor: pointer;
      background: var(--mat-sys-surface-container); }
    .tile img { width: 100%; height: 100%; object-fit: cover; display: block; }
    .tile.hidden img { opacity: .4; }
    .placeholder { display: grid; place-items: center; height: 100%; color: var(--mat-sys-on-surface-variant); }
    .badges { position: absolute; left: 4px; bottom: 4px; display: flex; gap: 2px; align-items: center;
      background: rgb(0 0 0 / .55); color: #fff; border-radius: 12px; padding: 1px 6px; font-size: 12px; }
    .badges mat-icon { font-size: 16px; width: 16px; height: 16px; }
    .faces { display: inline-flex; align-items: center; gap: 2px; }
    .bad { color: #ff8a80; }
    .sentinel { height: 40px; }
    .culling { display: flex; flex-wrap: wrap; gap: 12px; align-items: center; margin-bottom: 12px; }
    .panel.info { display: flex; align-items: center; gap: 8px; }
    .burst { display: flex; gap: 8px; overflow-x: auto; padding: 8px; margin-bottom: 10px; border-radius: 10px;
      background: var(--mat-sys-surface-container-low); }
    .burst-item { flex: 0 0 160px; display: flex; flex-direction: column; align-items: center; gap: 2px; }
    .burst-item .tile { width: 160px; }
    .tile.keeper { outline: 3px solid var(--mat-sys-primary); }
    .keep-label { display: inline-flex; align-items: center; gap: 2px; font-size: 13px; min-height: 36px; }
    .keep-label mat-icon { font-size: 18px; width: 18px; height: 18px; color: var(--mat-sys-primary); }
  `,
})
export class PhotosPage {
  protected readonly ctx = inject(EventContext);
  private readonly api = inject(AdminApi);
  private readonly dialog = inject(MatDialog);
  private readonly notify = inject(Notify);

  protected readonly collectionId = signal<string | null>(null);
  protected readonly status = signal<PhotoStatus | null>(null);
  protected readonly filter = signal<PhotoFilter>('All');
  protected readonly summary = signal<CullingSummary | null>(null);
  protected readonly photos = signal<Photo[]>([]);
  /** Duplicates view: one row per burst. */
  protected readonly bursts = computed(() => groupBursts(this.photos()));
  protected readonly total = signal(0);
  protected readonly loading = signal(false);
  private page = 0;
  private generation = 0;
  private readonly hasMore = computed(() => this.photos().length < this.total());
  private readonly sentinel = viewChild.required<ElementRef<HTMLElement>>('sentinel');

  constructor() {
    // Filters changed (or event loaded) → start over.
    effect(() => {
      const eventId = this.ctx.eventId();
      this.collectionId();
      this.status();
      this.filter();
      if (eventId) void this.reset();
    });
    // Chip counts depend on the event + collection only.
    effect(() => {
      const eventId = this.ctx.eventId();
      const collectionId = this.collectionId();
      if (eventId) untracked(() => void this.loadSummary(eventId, collectionId));
    });

    // Live: photos finished processing → refresh the grid (debounced), but only while the admin is still
    // looking at the first page — never yank the list away from someone who scrolled down.
    let liveTimer: ReturnType<typeof setTimeout> | undefined;
    effect(() => {
      if (this.ctx.photoTick() === 0) return;
      clearTimeout(liveTimer);
      liveTimer = setTimeout(() => untracked(() => {
        void this.refreshSummary();
        if (this.page <= 1 && !this.loading()) void this.reset();
      }), 2000);
    });
    inject(DestroyRef).onDestroy(() => clearTimeout(liveTimer));

    const destroyRef = inject(DestroyRef);
    afterNextRender(() => {
      const observer = new IntersectionObserver((entries) => {
        if (entries[0].isIntersecting && this.hasMore() && !this.loading()) void this.loadMore();
      }, { rootMargin: '400px' });
      observer.observe(this.sentinel().nativeElement);
      destroyRef.onDestroy(() => observer.disconnect());
    });
  }

  private async reset(): Promise<void> {
    this.generation++;
    this.page = 0;
    this.photos.set([]);
    this.total.set(0);
    await this.loadMore();
  }

  private async loadMore(): Promise<void> {
    const eventId = this.ctx.eventId();
    if (!eventId) return;
    const gen = this.generation;
    this.loading.set(true);
    try {
      const result = await this.api.photos(eventId, {
        collectionId: this.collectionId(), status: this.status(), filter: this.filter(), page: this.page + 1, pageSize: PAGE_SIZE,
      });
      if (gen !== this.generation) return; // filters changed meanwhile
      this.page = result.page;
      this.total.set(result.total);
      this.photos.update((list) => [...list, ...result.items]);
    } catch (err) {
      this.notify.error(err);
    } finally {
      this.loading.set(false);
    }
  }

  private async loadSummary(eventId: string, collectionId: string | null): Promise<void> {
    try {
      this.summary.set(await this.api.culling(eventId, collectionId));
    } catch {
      this.summary.set(null); // chips just show "…"
    }
  }

  private refreshSummary(): Promise<void> {
    const eventId = this.ctx.eventId();
    return eventId ? this.loadSummary(eventId, this.collectionId()) : Promise.resolve();
  }

  protected async analyzeExisting(): Promise<void> {
    try {
      const { queued } = await this.api.analyzeQuality(this.ctx.eventId()!);
      this.notify.ok(`Analyzing ${queued} photo(s) in the background — the counts update as they finish.`);
    } catch (err) {
      this.notify.error(err);
    }
  }

  protected async hideExtras(): Promise<void> {
    const n = this.summary()?.burstExtras ?? 0;
    if (!confirm(`Hide ${n} near-duplicate photo(s) from guests and keep only the best of each burst? You can show them again any time.`)) return;
    try {
      const { hidden } = await this.api.hideBurstExtras(this.ctx.eventId()!, this.collectionId());
      this.notify.ok(`${hidden} photo(s) hidden`);
      await this.refreshSummary();
      await this.reset();
    } catch (err) {
      this.notify.error(err);
    }
  }

  protected async keep(photo: Photo): Promise<void> {
    try {
      await this.api.keepInBurst(photo.id);
      // Update in place: this one becomes the keeper of its burst.
      this.photos.update((list) => list.map((p) => p.burstGroupId === photo.burstGroupId
        ? { ...p, isBurstBest: p.id === photo.id, isHidden: p.id === photo.id ? false : p.isHidden } : p));
    } catch (err) {
      this.notify.error(err);
    }
  }

  protected suggestHighlights(): void {
    this.dialog.open(SuggestHighlightsDialog, { data: this.ctx.eventId(), width: '900px', maxWidth: '96vw', autoFocus: false })
      .afterClosed()
      .subscribe((changed: boolean | undefined) => {
        if (!changed) return;
        void this.refreshSummary();
        void this.reset();
      });
  }

  protected open(photo: Photo): void {
    const data: PhotoDialogData = { photoId: photo.id, event: this.ctx.event.value()! };
    this.dialog.open(PhotoDialog, { data, maxWidth: '96vw', width: '1100px', autoFocus: false })
      .afterClosed()
      .subscribe((changed: boolean | undefined) => {
        if (changed) {
          this.ctx.reload();
          void this.refreshSummary();
          void this.reset();
        }
      });
  }
}
