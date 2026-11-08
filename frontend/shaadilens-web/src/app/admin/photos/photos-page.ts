import { Component, DestroyRef, ElementRef, afterNextRender, computed, effect, inject, signal, untracked, viewChild } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { MatButtonModule } from '@angular/material/button';
import { MatButtonToggleModule } from '@angular/material/button-toggle';
import { MatDialog } from '@angular/material/dialog';
import { MatFormFieldModule } from '@angular/material/form-field';
import { MatIconModule } from '@angular/material/icon';
import { MatProgressBarModule } from '@angular/material/progress-bar';
import { MatSelectModule } from '@angular/material/select';
import { MatTooltipModule } from '@angular/material/tooltip';
import { AdminApi } from '../../core/api/admin-api';
import { Photo, PhotoStatus } from '../../core/api/api.models';
import { Notify } from '../../core/notify';
import { EventContext } from '../event/event-context';
import { PhotoDialog, PhotoDialogData } from './photo-dialog';

const PAGE_SIZE = 60;

@Component({
  selector: 'app-photos-page',
  imports: [FormsModule, MatButtonModule, MatButtonToggleModule, MatFormFieldModule, MatIconModule, MatProgressBarModule, MatSelectModule, MatTooltipModule],
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

    @if (loading() && !photos().length) {
      <mat-progress-bar mode="indeterminate" />
    }

    <div class="grid">
      @for (p of photos(); track p.id) {
        <button class="tile" (click)="open(p)" [class.hidden]="p.isHidden" [attr.aria-label]="p.fileName">
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
          </span>
        </button>
      } @empty {
        @if (!loading()) { <p class="muted">No photos match these filters.</p> }
      }
    </div>

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
  `,
})
export class PhotosPage {
  protected readonly ctx = inject(EventContext);
  private readonly api = inject(AdminApi);
  private readonly dialog = inject(MatDialog);
  private readonly notify = inject(Notify);

  protected readonly collectionId = signal<string | null>(null);
  protected readonly status = signal<PhotoStatus | null>(null);
  protected readonly photos = signal<Photo[]>([]);
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
      if (eventId) void this.reset();
    });

    // Live: photos finished processing → refresh the grid (debounced), but only while the admin is still
    // looking at the first page — never yank the list away from someone who scrolled down.
    let liveTimer: ReturnType<typeof setTimeout> | undefined;
    effect(() => {
      if (this.ctx.photoTick() === 0) return;
      clearTimeout(liveTimer);
      liveTimer = setTimeout(() => untracked(() => { if (this.page <= 1 && !this.loading()) void this.reset(); }), 2000);
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
      const result = await this.api.photos(eventId, { collectionId: this.collectionId(), status: this.status(), page: this.page + 1, pageSize: PAGE_SIZE });
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

  protected open(photo: Photo): void {
    const data: PhotoDialogData = { photoId: photo.id, event: this.ctx.event.value()! };
    this.dialog.open(PhotoDialog, { data, maxWidth: '96vw', width: '1100px', autoFocus: false })
      .afterClosed()
      .subscribe((changed: boolean | undefined) => {
        if (changed) {
          this.ctx.reload();
          void this.reset();
        }
      });
  }
}
