import { DecimalPipe, NgTemplateOutlet } from '@angular/common';
import { Component, DestroyRef, computed, effect, inject, input, signal, untracked } from '@angular/core';
import { MatButtonModule } from '@angular/material/button';
import { MatIconModule } from '@angular/material/icon';
import { MatProgressBarModule } from '@angular/material/progress-bar';
import { GuestApi, ReelState } from '../../core/api/guest-api';
import { Notify } from '../../core/notify';

/**
 * "Your highlight reel": a ~30 s portrait video (9:16, for WhatsApp status / Instagram) made from the guest's best
 * photos. The server picks the photos and renders in the background (~1 minute); this card shows progress, then
 * the video with Share / Download. `tick` changes when the server says the reel changed (SignalR); while a reel
 * is being made it also polls, in case the live connection is down.
 */
@Component({
  selector: 'app-reel-card',
  imports: [DecimalPipe, NgTemplateOutlet, MatButtonModule, MatIconModule, MatProgressBarModule],
  template: `
    @if (visible(); as s) {
      <section class="card">
        @switch (s.latest?.status) {
          @case ('Ready') {
            <div class="ready">
              <video [src]="s.latest!.url + '#t=0.1'" controls playsinline preload="metadata" class="video"></video>
              <div class="side">
                <h2><mat-icon>movie</mat-icon> Your highlight reel</h2>
                <p class="hint">{{ s.latest!.photoCount }} photos · {{ s.latest!.durationSeconds | number: '1.0-0' }} s</p>
                @if (canShareFiles) {
                  <button mat-flat-button (click)="share()" [disabled]="busy()"><mat-icon>share</mat-icon> Share</button>
                }
                <a mat-stroked-button [href]="s.latest!.downloadUrl" download><mat-icon>download</mat-icon> Download</a>
                @if (s.available) {
                  <button mat-button (click)="make()" [disabled]="busy()"><mat-icon>autorenew</mat-icon> Make a new one ({{ s.remainingToday }} left today)</button>
                }
              </div>
            </div>
          }
          @case ('Queued') { <ng-container *ngTemplateOutlet="working" /> }
          @case ('Rendering') { <ng-container *ngTemplateOutlet="working" /> }
          @default {
            <mat-icon class="big">movie_filter</mat-icon>
            <div class="text">
              <h2>Your highlight reel</h2>
              @if (s.latest?.status === 'Failed') {
                <p class="error">{{ s.latest!.error }}</p>
              } @else {
                <p>A 30-second video of your best photos, with music — ready to post on WhatsApp or Instagram.</p>
              }
              @if (!s.available && s.reason) { <p class="hint">{{ s.reason }}</p> }
            </div>
            <button mat-flat-button (click)="make()" [disabled]="!s.available || busy()">
              <mat-icon>auto_awesome</mat-icon> {{ s.latest?.status === 'Failed' ? 'Try again' : 'Make my reel' }}
            </button>
          }
        }
        <ng-template #working>
          <mat-icon class="big spin">movie_filter</mat-icon>
          <div class="text">
            <h2>Making your reel…</h2>
            <p>Picking your best photos and adding music. It takes about 2 minutes — you can keep browsing.</p>
            <mat-progress-bar mode="indeterminate" />
          </div>
        </ng-template>
      </section>
    }
  `,
  styles: `
    .card { display: flex; align-items: center; gap: 12px; margin: 0 16px 12px; padding: 12px 14px; border-radius: 16px;
      background: var(--mat-sys-surface-container); flex-wrap: wrap; }
    .card h2 { font: var(--mat-sys-title-medium); margin: 0 0 4px; display: flex; align-items: center; gap: 6px; }
    .card p { margin: 0; }
    .text { flex: 1; min-width: 200px; display: flex; flex-direction: column; gap: 6px; }
    .big { font-size: 36px; width: 36px; height: 36px; color: var(--mat-sys-primary); flex: none; }
    .spin { animation: pulse 1.4s ease-in-out infinite; }
    @keyframes pulse { 50% { opacity: .35; } }
    .hint { font: var(--mat-sys-body-small); color: var(--mat-sys-on-surface-variant); }
    .error { color: var(--mat-sys-error); }
    .ready { display: flex; gap: 16px; flex-wrap: wrap; width: 100%; }
    .video { width: min(100%, 300px); aspect-ratio: 9 / 16; max-height: 60vh; border-radius: 12px; background: #000; }
    .side { display: flex; flex-direction: column; gap: 8px; align-items: flex-start; justify-content: center; }
    button[mat-flat-button] { border-radius: 20px; }
  `,
})
export class ReelCard {
  private readonly api = inject(GuestApi);
  private readonly notify = inject(Notify);

  /** Bumped by the gallery when SignalR says "ReelUpdated". */
  readonly tick = input(0);

  protected readonly state = signal<ReelState | null>(null);
  protected readonly busy = signal(false);
  /** Hide the card entirely until the guest has enough photos (or already has a reel). */
  protected readonly visible = computed(() => {
    const s = this.state();
    return s && s.enabled && (s.latest || s.photosAvailable >= 3) ? s : null;
  });
  protected readonly canShareFiles = typeof navigator !== 'undefined' && 'canShare' in navigator
    && navigator.canShare({ files: [new File([], 'x.mp4', { type: 'video/mp4' })] });
  private poll: ReturnType<typeof setInterval> | undefined;

  constructor() {
    effect(() => {
      this.tick();
      untracked(() => void this.load());
    });
    inject(DestroyRef).onDestroy(() => clearInterval(this.poll));
  }

  private async load(): Promise<void> {
    try {
      const s = await this.api.reel();
      this.state.set(s);
      const working = s.latest?.status === 'Queued' || s.latest?.status === 'Rendering';
      clearInterval(this.poll);
      if (working) this.poll = setInterval(() => void this.load(), 5000);
    } catch {
      // Reels are optional: no card if the endpoint isn't reachable.
    }
  }

  protected async make(): Promise<void> {
    this.busy.set(true);
    try {
      await this.api.makeReel();
      await this.load();
    } catch (err) {
      this.notify.error(err);
      await this.load();
    } finally {
      this.busy.set(false);
    }
  }

  /** Phone share sheet with the video file itself (WhatsApp, Instagram…). */
  protected async share(): Promise<void> {
    const reel = this.state()?.latest;
    if (!reel?.downloadUrl) return;
    this.busy.set(true);
    try {
      const blob = await (await fetch(reel.downloadUrl)).blob();
      const file = new File([blob], 'highlights.mp4', { type: 'video/mp4' });
      await navigator.share({ files: [file], title: 'My highlights' });
    } catch (err) {
      if ((err as DOMException)?.name !== 'AbortError') this.notify.error(err);
    } finally {
      this.busy.set(false);
    }
  }
}
