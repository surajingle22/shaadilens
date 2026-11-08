import { HttpErrorResponse } from '@angular/common/http';
import { Component, DestroyRef, HostListener, computed, effect, inject, input, signal, untracked } from '@angular/core';
import { MatIconModule } from '@angular/material/icon';
import { Title } from '@angular/platform-browser';
import { ActivatedRoute } from '@angular/router';
import { GuestApi, GuestCollection, GuestPhoto } from '../core/api/guest-api';
import { GuestSession } from '../core/guest/guest-session';
import { errorMessage } from '../core/notify';
import { Realtime } from '../core/realtime/realtime';
import { PinStep } from '../guest/steps/pin-step';

const PAGE = 100;
const MAX_PHOTOS = 5000;

/**
 * /tv/:token — fullscreen slideshow for a TV or projector at the venue, using a Full-gallery link.
 *
 *  ?interval=8          seconds per photo (default 7)
 *  ?collection=Sangeet  only one collection (name or id)
 *
 * Keys (and most TV remotes, which send arrow keys/Enter): → ← next/previous · Space/Enter pause ·
 * F fullscreen · C next collection. New photos arrive live (SignalR) and are shown next.
 * Signed photo URLs expire after an hour, so the list is refreshed every 20 minutes.
 */
@Component({
  selector: 'app-tv-page',
  imports: [MatIconModule, PinStep],
  providers: [Realtime],
  template: `
    <div class="tv" [class.idle]="idle()" [class.paused]="paused()" [style.--title-font]="titleFont()" (mousemove)="wake()" (click)="toggleFullscreen()">
      @switch (state()) {
        @case ('pin') { <div class="center light"><app-pin-step [token]="token()" (verified)="start($event)" /></div> }
        @case ('error') { <div class="center"><mat-icon>tv_off</mat-icon><p>{{ message() }}</p></div> }
        @case ('loading') { <div class="center"><p class="title">{{ eventName() || 'Loading…' }}</p></div> }
        @default {
          @if (!photos().length) {
            <div class="center"><p class="title">{{ eventName() }}</p><p>Photos will appear here as soon as they’re uploaded.</p></div>
          }
          @for (layer of layers(); track layer.key) {
            <img class="slide" [class.shown]="layer.shown" [src]="layer.photo.webUrl" alt=""
                 [style.animation-duration.s]="interval() + 1.5" (load)="loaded(layer.key)" />
          }
          <footer class="caption">
            <span class="title">{{ eventName() }}</span>
            @if (collectionName()) { <span> · {{ collectionName() }}</span> }
          </footer>
          @if (paused()) { <div class="badge"><mat-icon>pause</mat-icon> Paused</div> }
          <div class="help">← → photos · Space pause · F fullscreen · C collection ({{ collectionName() || 'All' }})</div>
        }
      }
    </div>
  `,
  styles: `
    :host { display: block; }
    .tv { position: fixed; inset: 0; background: #000; color: #eee; overflow: hidden; font-family: Roboto, sans-serif; }
    .tv.idle { cursor: none; }
    .tv.paused .slide { animation-play-state: paused; }
    .center { height: 100%; display: flex; flex-direction: column; align-items: center; justify-content: center; gap: 12px; text-align: center; padding: 24px; }
    .center mat-icon { font-size: 64px; width: 64px; height: 64px; }
    .center.light { background: #fff; color: #222; }
    .title { font-family: var(--title-font, Georgia, serif); font-size: clamp(28px, 4vw, 64px); margin: 0; }
    .slide { position: absolute; inset: 0; width: 100%; height: 100%; object-fit: contain; opacity: 0; transition: opacity 1.2s ease-in-out; }
    .slide.shown { opacity: 1; animation: kenburns linear forwards; }
    @keyframes kenburns { from { transform: scale(1); } to { transform: scale(1.05); } }
    .caption { position: absolute; left: 0; right: 0; bottom: 0; padding: 40px 32px 20px; background: linear-gradient(transparent, rgb(0 0 0 / .6));
      font-size: clamp(14px, 1.4vw, 22px); }
    .caption .title { font-size: clamp(20px, 2.2vw, 36px); }
    .badge { position: absolute; top: 24px; right: 24px; display: flex; align-items: center; gap: 6px; padding: 8px 14px; border-radius: 20px; background: rgb(0 0 0 / .6); }
    .help { position: absolute; top: 24px; left: 24px; padding: 6px 12px; border-radius: 8px; background: rgb(0 0 0 / .5); font-size: 14px; transition: opacity .4s; }
    .idle .help { opacity: 0; }
  `,
})
export class TvPage {
  private readonly api = inject(GuestApi);
  private readonly session = inject(GuestSession);
  private readonly realtime = inject(Realtime);
  private readonly route = inject(ActivatedRoute);
  private readonly title = inject(Title);

  readonly token = input.required<string>();

  protected readonly state = signal<'loading' | 'pin' | 'error' | 'show'>('loading');
  protected readonly message = signal('');
  protected readonly eventName = signal('');
  protected readonly titleFont = signal<string | null>(null);
  protected readonly photos = signal<GuestPhoto[]>([]);
  protected readonly collections = signal<GuestCollection[]>([]);
  protected readonly collectionId = signal<string | null>(null);
  protected readonly collectionName = computed(() => this.collections().find((c) => c.id === this.collectionId())?.name ?? null);
  protected readonly index = signal(0);
  protected readonly paused = signal(false);
  protected readonly idle = signal(true);
  protected readonly interval = signal(Math.min(60, Math.max(3, Number(this.route.snapshot.queryParamMap.get('interval')) || 7)));

  /** Two stacked images: the next one fades in over the current one once it has loaded. */
  protected readonly layers = signal<{ key: number; photo: GuestPhoto; shown: boolean }[]>([]);
  private layerKey = 0;
  /** New photos (live) to show before continuing the normal order. */
  private readonly upNext: string[] = [];
  private timer: ReturnType<typeof setTimeout> | undefined;
  private idleTimer: ReturnType<typeof setTimeout> | undefined;
  private wakeLock: { release(): Promise<void> } | null = null;

  constructor() {
    effect(() => {
      const token = this.token();
      untracked(() => void this.boot(token));
    });
    const refresh = setInterval(() => void this.reload(), 20 * 60_000);
    inject(DestroyRef).onDestroy(() => {
      clearInterval(refresh);
      clearTimeout(this.timer);
      clearTimeout(this.idleTimer);
      void this.wakeLock?.release().catch(() => undefined);
    });
  }

  private async boot(token: string): Promise<void> {
    try {
      if (!this.session.open(token)) {
        const link = await this.api.link(token);
        this.eventName.set(link.event.name);
        if (link.linkType !== 'Full' && link.linkType !== 'Selection') {
          return this.fail('The TV slideshow needs a “Full gallery” link. Create one on the Share tab.');
        }
        if (link.requiresPin) return this.state.set('pin');
      }
      await this.start(null);
    } catch (err) {
      this.fail(err instanceof HttpErrorResponse && err.status === 404 ? 'This link doesn’t exist.' : errorMessage(err));
    }
  }

  /** Get a viewer session (no form for a TV), then load photos and go live. */
  protected async start(pinTicket: string | null): Promise<void> {
    this.state.set('loading');
    try {
      if (!this.session.token()) {
        const login = await this.api.register(this.token(), { consent: false, pinTicket });
        this.session.save(this.token(), login.token, login.expiresAt);
      }
      const me = await this.api.me();
      this.eventName.set(me.event.name);
      this.titleFont.set(me.event.titleFont ? `'${me.event.titleFont}', Georgia, serif` : null);
      this.title.setTitle(`${me.event.name} · TV`);
      this.collections.set(await this.api.collections());
      const wanted = this.route.snapshot.queryParamMap.get('collection');
      const c = this.collections().find((x) => x.id === wanted || x.name.toLowerCase() === wanted?.toLowerCase());
      this.collectionId.set(c?.id ?? null);
      await this.reload();
      this.state.set('show');
      this.show(0);
      await this.realtime.connect(this.session.token()!);
      this.realtime.on('PhotoAdded', (m) => { this.upNext.push(m.photoId); void this.reload(); });
      this.wakeLock = await (navigator as Navigator & { wakeLock?: { request(t: 'screen'): Promise<{ release(): Promise<void> }> } })
        .wakeLock?.request('screen').catch(() => null) ?? null;
    } catch (err) {
      const status = (err as { status?: number }).status;
      this.fail(status === 400
        ? 'This link asks guests to fill a form. For the TV, use a Full gallery link with “Ask guests to fill a form” turned off.'
        : errorMessage(err));
    }
  }

  private fail(message: string): void {
    this.message.set(message);
    this.state.set('error');
  }

  /** (Re)load every photo of the current collection, keeping the current one on screen. */
  private async reload(): Promise<void> {
    const current = this.photos()[this.index()]?.id;
    const all: GuestPhoto[] = [];
    for (let page = 1; all.length < MAX_PHOTOS; page++) {
      const r = await this.api.photos('gallery', this.collectionId(), page, PAGE);
      all.push(...r.items);
      if (all.length >= r.total || !r.items.length) break;
    }
    this.photos.set(all);
    const at = all.findIndex((p) => p.id === current);
    this.index.set(at >= 0 ? at : 0);
    if (this.state() === 'show' && !this.layers().length && all.length) this.show(0);
  }

  private show(i: number): void {
    const list = this.photos();
    if (!list.length) return;
    const index = ((i % list.length) + list.length) % list.length;
    this.index.set(index);
    const key = ++this.layerKey;
    // Keep at most the previous layer underneath for the cross-fade.
    this.layers.update((l) => [...l.filter((x) => x.shown).slice(-1), { key, photo: list[index], shown: false }]);
    this.schedule();
  }

  protected loaded(key: number): void {
    this.layers.update((l) => l.map((x) => (x.key === key ? { ...x, shown: true } : x)));
    setTimeout(() => this.layers.update((l) => l.filter((x) => x.key >= key)), 1300); // after the fade
  }

  private schedule(): void {
    clearTimeout(this.timer);
    if (!this.paused()) this.timer = setTimeout(() => this.next(), this.interval() * 1000);
  }

  private next(): void {
    while (this.upNext.length) {
      const i = this.photos().findIndex((p) => p.id === this.upNext[0]);
      this.upNext.shift();
      if (i >= 0) return this.show(i);
    }
    this.show(this.index() + 1);
  }

  private async nextCollection(): Promise<void> {
    const ids = [null, ...this.collections().map((c) => c.id)];
    this.collectionId.set(ids[(ids.indexOf(this.collectionId()) + 1) % ids.length]);
    this.layers.set([]);
    await this.reload();
    this.show(0);
  }

  protected wake(): void {
    this.idle.set(false);
    clearTimeout(this.idleTimer);
    this.idleTimer = setTimeout(() => this.idle.set(true), 3000);
  }

  protected toggleFullscreen(): void {
    if (document.fullscreenElement) void document.exitFullscreen();
    else void document.documentElement.requestFullscreen?.().catch(() => undefined);
  }

  @HostListener('document:keydown', ['$event'])
  protected key(e: KeyboardEvent): void {
    if (this.state() !== 'show') return;
    this.wake();
    switch (e.key) {
      case 'ArrowRight': case 'PageDown': this.show(this.index() + 1); break;
      case 'ArrowLeft': case 'PageUp': this.show(this.index() - 1); break;
      case ' ': case 'Enter': case 'MediaPlayPause':
        this.paused.update((p) => !p);
        this.schedule();
        break;
      case 'f': case 'F': this.toggleFullscreen(); break;
      case 'c': case 'C': void this.nextCollection(); break;
      default: return;
    }
    e.preventDefault();
  }
}
