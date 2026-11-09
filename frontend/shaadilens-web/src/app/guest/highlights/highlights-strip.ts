import { Component, DestroyRef, HostListener, computed, inject, resource, signal } from '@angular/core';
import { MatIconModule } from '@angular/material/icon';
import { GuestApi } from '../../core/api/guest-api';
import { StoryCursor, nextCursor, prevCursor } from './story-cursor';

const SECONDS_PER_PHOTO = 5;

/**
 * Highlights strip, like "stories": one round bubble per collection with highlights (photos the couple /
 * photographer marked). Tap → fullscreen viewer that plays them, with a progress bar per photo.
 * Tap right/left half = next/previous, press and hold = pause, swipe down or ✕ = close. Keys: ← → Esc Space.
 */
@Component({
  selector: 'app-highlights-strip',
  imports: [MatIconModule],
  template: `
    @if (stories().length) {
      <nav class="strip" aria-label="Highlights">
        @for (s of stories(); track s.collectionId; let i = $index) {
          <button class="bubble" (click)="open(i)" [attr.aria-label]="'Highlights: ' + s.name">
            <span class="ring" [class.seen]="seen().has(s.collectionId)"><img [src]="s.photos[0].thumbUrl" alt="" loading="lazy" /></span>
            <span class="name">{{ s.name }}</span>
          </button>
        }
      </nav>
    }

    @if (cursor(); as c) {
      <div class="viewer" role="dialog" aria-label="Highlights" (pointerdown)="hold($event)" (pointerup)="release($event)"
           (pointercancel)="release($event)">
        <div class="bars">
          @for (p of stories()[c.story].photos; track p.id; let j = $index) {
            <span class="bar"><span class="fill" [class.done]="j < c.photo" [class.run]="j === c.photo && running()" [class.paused]="paused()"
              [style.animation-duration.s]="seconds"></span></span>
          }
        </div>
        <header>
          <span class="title">{{ stories()[c.story].name }}</span>
          <button class="close" (click)="close(); $event.stopPropagation()" (pointerdown)="$event.stopPropagation()" aria-label="Close">
            <mat-icon>close</mat-icon>
          </button>
        </header>
        @if (current(); as p) {
          <!-- key: a new <img> per photo so the progress animation restarts with it -->
          @for (k of [p.id]; track k) {
            <img class="photo" [src]="p.webUrl" alt="" (load)="loaded()" draggable="false" />
          }
        }
        @if (next(); as n) { <link rel="prefetch" [href]="n.webUrl" /> }
      </div>
    }
  `,
  styles: `
    :host { display: block; }
    .strip { display: flex; gap: 14px; overflow-x: auto; padding: 4px 16px 12px; scrollbar-width: none; }
    .strip::-webkit-scrollbar { display: none; }
    .bubble { flex: none; display: flex; flex-direction: column; align-items: center; gap: 4px; background: none; border: 0;
      padding: 0; cursor: pointer; color: inherit; width: 72px; }
    .ring { width: 66px; height: 66px; border-radius: 50%; padding: 3px; display: block;
      background: conic-gradient(var(--mat-sys-primary), #f9a825, var(--mat-sys-primary)); }
    .ring.seen { background: var(--mat-sys-outline-variant); }
    .ring img { width: 100%; height: 100%; border-radius: 50%; object-fit: cover; border: 2px solid var(--mat-sys-surface); display: block; }
    .name { font: var(--mat-sys-label-small); max-width: 72px; overflow: hidden; text-overflow: ellipsis; white-space: nowrap; }
    .viewer { position: fixed; inset: 0; z-index: 1000; background: #000; color: #fff; touch-action: none; user-select: none; }
    .photo { position: absolute; inset: 0; width: 100%; height: 100%; object-fit: contain; }
    .bars { position: absolute; top: 8px; left: 8px; right: 8px; display: flex; gap: 4px; z-index: 2; }
    .bar { flex: 1; height: 3px; border-radius: 2px; background: rgb(255 255 255 / .35); overflow: hidden; }
    .fill { display: block; height: 100%; width: 0; background: #fff; }
    .fill.done { width: 100%; }
    .fill.run { animation: grow linear forwards; }
    .fill.paused { animation-play-state: paused; }
    @keyframes grow { from { width: 0; } to { width: 100%; } }
    header { position: absolute; top: 18px; left: 12px; right: 4px; display: flex; align-items: center; z-index: 2;
      text-shadow: 0 1px 3px rgb(0 0 0 / .6); }
    .title { flex: 1; font: var(--mat-sys-title-small); }
    .close { background: none; border: 0; color: #fff; padding: 8px; cursor: pointer; }
  `,
})
export class HighlightsStrip {
  private readonly api = inject(GuestApi);

  private readonly data = resource({ loader: () => this.api.highlights().catch(() => []) });
  protected readonly stories = computed(() => (this.data.value() ?? []).filter((s) => s.photos.length));
  protected readonly cursor = signal<StoryCursor | null>(null);
  protected readonly paused = signal(false);
  /** The current photo has loaded and its timer runs (the progress bar starts with it). */
  protected readonly running = signal(false);
  protected readonly seen = signal(new Set<string>());
  protected readonly seconds = SECONDS_PER_PHOTO;
  protected readonly current = computed(() => {
    const c = this.cursor();
    return c ? this.stories()[c.story]?.photos[c.photo] ?? null : null;
  });
  protected readonly next = computed(() => {
    const c = this.cursor();
    const n = c ? nextCursor(this.sizes(), c) : null;
    return n ? this.stories()[n.story].photos[n.photo] : null;
  });
  private readonly sizes = computed(() => this.stories().map((s) => s.photos.length));

  private timer: ReturnType<typeof setTimeout> | undefined;
  private remaining = SECONDS_PER_PHOTO * 1000;
  private startedAt = 0;
  private downAt: { x: number; y: number; t: number } | null = null;

  constructor() {
    inject(DestroyRef).onDestroy(() => clearTimeout(this.timer));
  }

  protected open(story: number): void {
    this.go({ story, photo: 0 });
  }

  protected close(): void {
    clearTimeout(this.timer);
    this.cursor.set(null);
    this.paused.set(false);
  }

  private go(c: StoryCursor | null): void {
    clearTimeout(this.timer);
    if (!c) return this.close();
    const id = this.stories()[c.story].collectionId;
    this.seen.update((s) => new Set(s).add(id));
    this.paused.set(false);
    this.running.set(false);
    this.cursor.set(c);
    this.remaining = SECONDS_PER_PHOTO * 1000;
    // The timer starts when the image has loaded (see loaded()), so slow connections don't skip photos.
  }

  protected loaded(): void {
    this.running.set(true);
    this.startTimer();
  }

  private startTimer(): void {
    clearTimeout(this.timer);
    this.startedAt = Date.now();
    this.timer = setTimeout(() => this.forward(), this.remaining);
  }

  private forward(): void {
    const c = this.cursor();
    if (c) this.go(nextCursor(this.sizes(), c));
  }

  private back(): void {
    const c = this.cursor();
    if (c) this.go(prevCursor(this.sizes(), c));
  }

  /** Press and hold pauses; a quick tap navigates (on release). */
  protected hold(e: PointerEvent): void {
    this.downAt = { x: e.clientX, y: e.clientY, t: Date.now() };
    clearTimeout(this.timer);
    this.remaining = Math.max(0, this.remaining - (Date.now() - this.startedAt));
    this.paused.set(true);
  }

  protected release(e: PointerEvent): void {
    const down = this.downAt;
    this.downAt = null;
    if (!down) return;
    if (e.clientY - down.y > 80) return this.close(); // swipe down
    const tap = Date.now() - down.t < 300;
    if (tap) return e.clientX < window.innerWidth / 3 ? this.back() : this.forward();
    this.paused.set(false);
    this.startTimer();
  }

  @HostListener('document:keydown', ['$event'])
  protected key(e: KeyboardEvent): void {
    if (!this.cursor()) return;
    if (e.key === 'Escape') this.close();
    else if (e.key === 'ArrowRight') this.forward();
    else if (e.key === 'ArrowLeft') this.back();
    else return;
    e.preventDefault();
  }
}
