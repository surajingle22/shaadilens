import { Component, HostListener, computed, input, output, signal } from '@angular/core';
import { MatButtonModule } from '@angular/material/button';
import { MatIconModule } from '@angular/material/icon';
import { GuestPhoto } from '../../core/api/guest-api';

/**
 * Full-screen photo viewer.
 *  - swipe left/right (or ←/→ keys) to move, swipe down or Esc to close
 *  - pinch with two fingers or double-tap to zoom; drag to pan while zoomed
 * Built on Pointer Events, so it works the same for touch, mouse and pen.
 */
@Component({
  selector: 'app-lightbox',
  imports: [MatButtonModule, MatIconModule],
  template: `
    <div class="lightbox" role="dialog" aria-modal="true" aria-label="Photo viewer">
      <header>
        <button mat-icon-button (click)="closed.emit()" aria-label="Close"><mat-icon>close</mat-icon></button>
        <span class="count">{{ index() + 1 }} / {{ photos().length }}</span>
        <span class="tools">
          @if (feedback() === 'mine') {
            <button mat-button class="not-me" (click)="reject.emit(current())"><mat-icon>person_off</mat-icon> Not me</button>
          }
          @if (canDownload()) {
            <button mat-icon-button (click)="download.emit(current())" aria-label="Download"><mat-icon>download</mat-icon></button>
          }
        </span>
      </header>

      <div class="stage"
           (pointerdown)="down($event)" (pointermove)="move($event)" (pointerup)="up($event)" (pointercancel)="up($event)"
           (dblclick)="toggleZoom()">
        <img [src]="current().webUrl" [style.transform]="transform()" [class.animate]="!dragging()" alt="Photo" draggable="false" />
      </div>

      @if (index() > 0) {
        <button mat-icon-button class="nav prev" (click)="go(-1)" aria-label="Previous"><mat-icon>chevron_left</mat-icon></button>
      }
      @if (index() < photos().length - 1) {
        <button mat-icon-button class="nav next" (click)="go(1)" aria-label="Next"><mat-icon>chevron_right</mat-icon></button>
      }
      @if (feedback() === 'maybe') {
        <footer class="ask">
          <span>Is this you?</span>
          <button mat-stroked-button (click)="reject.emit(current())"><mat-icon>close</mat-icon> Not me</button>
          <button mat-flat-button (click)="confirm.emit(current())"><mat-icon>check</mat-icon> Yes, that’s me</button>
        </footer>
      }
      <ng-content />
    </div>
  `,
  styles: `
    .lightbox { position: fixed; inset: 0; z-index: 1000; background: #000; color: #fff; display: flex; flex-direction: column; }
    header { display: flex; align-items: center; justify-content: space-between; padding: 4px; position: absolute; top: 0; left: 0; right: 0; z-index: 2;
      background: linear-gradient(rgb(0 0 0 / .6), transparent); }
    header button, .nav { color: #fff; }
    .count { font: var(--mat-sys-label-large); }
    .stage { flex: 1; display: flex; align-items: center; justify-content: center; overflow: hidden; touch-action: none; user-select: none; }
    img { max-width: 100%; max-height: 100%; object-fit: contain; will-change: transform; }
    img.animate { transition: transform .2s ease-out; }
    .nav { position: absolute; top: 50%; translate: 0 -50%; background: rgb(0 0 0 / .35); }
    .prev { left: 8px; }
    .next { right: 8px; }
    .tools { display: flex; align-items: center; gap: 4px; }
    .not-me { color: #fff; }
    .ask { position: absolute; left: 0; right: 0; bottom: 0; z-index: 2; display: flex; flex-wrap: wrap; align-items: center; justify-content: center;
      gap: 8px; padding: 16px 12px calc(16px + env(safe-area-inset-bottom)); background: linear-gradient(transparent, rgb(0 0 0 / .75)); }
    .ask span { width: 100%; text-align: center; font: var(--mat-sys-title-medium); }
    .ask button { min-width: 140px; height: 44px; border-radius: 22px; }
    .ask [mat-stroked-button] { color: #fff; border-color: rgb(255 255 255 / .6); }
    @media (pointer: coarse) { .nav { display: none; } }
  `,
})
export class Lightbox {
  readonly photos = input.required<GuestPhoto[]>();
  readonly startIndex = input(0);
  readonly canDownload = input(false);
  /** Which "is this you?" buttons to show: none (Full gallery), Not me ("Your photos"), or both ("Might be you?"). */
  readonly feedback = input<'none' | 'mine' | 'maybe'>('none');
  readonly confirm = output<GuestPhoto>();
  readonly reject = output<GuestPhoto>();
  readonly closed = output<void>();
  readonly download = output<GuestPhoto>();
  /** Asks the parent to load more photos when we reach the end of what's loaded. */
  readonly nearEnd = output<void>();

  private readonly offset = signal<number | null>(null);
  protected readonly index = computed(() => Math.min(this.offset() ?? this.startIndex(), this.photos().length - 1));
  protected readonly current = computed(() => this.photos()[this.index()]);

  // Gesture state
  private readonly scale = signal(1);
  private readonly tx = signal(0);
  private readonly ty = signal(0);
  protected readonly dragging = signal(false);
  protected readonly transform = computed(() => `translate(${this.tx()}px, ${this.ty()}px) scale(${this.scale()})`);
  private readonly pointers = new Map<number, { x: number; y: number }>();
  private start = { x: 0, y: 0, tx: 0, ty: 0, dist: 0, scale: 1 };

  protected go(delta: number): void {
    const next = this.index() + delta;
    if (next < 0 || next >= this.photos().length) return;
    this.offset.set(next);
    this.resetZoom();
    if (next >= this.photos().length - 3) this.nearEnd.emit();
  }

  protected toggleZoom(): void {
    if (this.scale() > 1) this.resetZoom();
    else this.scale.set(2.5);
  }

  private resetZoom(): void {
    this.scale.set(1);
    this.tx.set(0);
    this.ty.set(0);
  }

  protected down(e: PointerEvent): void {
    (e.target as HTMLElement).setPointerCapture?.(e.pointerId);
    this.pointers.set(e.pointerId, { x: e.clientX, y: e.clientY });
    this.dragging.set(true);
    const pts = [...this.pointers.values()];
    this.start = {
      x: e.clientX, y: e.clientY, tx: this.tx(), ty: this.ty(),
      dist: pts.length === 2 ? Math.hypot(pts[0].x - pts[1].x, pts[0].y - pts[1].y) : 0,
      scale: this.scale(),
    };
  }

  protected move(e: PointerEvent): void {
    if (!this.pointers.has(e.pointerId)) return;
    this.pointers.set(e.pointerId, { x: e.clientX, y: e.clientY });
    const pts = [...this.pointers.values()];
    if (pts.length === 2 && this.start.dist > 0) {
      // Pinch: scale by how much the distance between the two fingers changed.
      const dist = Math.hypot(pts[0].x - pts[1].x, pts[0].y - pts[1].y);
      this.scale.set(Math.min(5, Math.max(1, this.start.scale * (dist / this.start.dist))));
    } else if (pts.length === 1) {
      const dx = e.clientX - this.start.x;
      const dy = e.clientY - this.start.y;
      if (this.scale() > 1) {
        this.tx.set(this.start.tx + dx); // pan
        this.ty.set(this.start.ty + dy);
      } else {
        this.tx.set(dx); // follow the finger while swiping
        this.ty.set(Math.max(0, dy));
      }
    }
  }

  protected up(e: PointerEvent): void {
    this.pointers.delete(e.pointerId);
    if (this.pointers.size > 0) return;
    this.dragging.set(false);
    if (this.scale() > 1) return;
    const dx = this.tx();
    const dy = this.ty();
    this.resetZoom();
    if (dy > 120 && Math.abs(dx) < dy) this.closed.emit();
    else if (dx < -60) this.go(1);
    else if (dx > 60) this.go(-1);
  }

  @HostListener('document:keydown', ['$event'])
  protected key(e: KeyboardEvent): void {
    if (e.key === 'ArrowRight') this.go(1);
    else if (e.key === 'ArrowLeft') this.go(-1);
    else if (e.key === 'Escape') this.closed.emit();
  }
}
