import { Component, DestroyRef, ElementRef, afterNextRender, computed, effect, inject, input, output, resource, signal, untracked, viewChild } from '@angular/core';
import { MatButtonModule } from '@angular/material/button';
import { MatChipsModule } from '@angular/material/chips';
import { MatIconModule } from '@angular/material/icon';
import { MatMenuModule } from '@angular/material/menu';
import { MatProgressBarModule } from '@angular/material/progress-bar';
import { GuestApi, GuestPhoto, GuestSessionInfo, SelectionState } from '../../core/api/guest-api';
import { HttpErrorResponse } from '@angular/common/http';
import { Notify, errorMessage } from '../../core/notify';
import { SelfieStep } from '../steps/selfie-step';
import { GuestSession } from '../../core/guest/guest-session';
import { Realtime } from '../../core/realtime/realtime';
import { Lightbox } from './lightbox';
import { NotifyCard } from './notify-card';

const PAGE_SIZE = 60;

/**
 * Guest links: "Your photos" (Auto/Confirmed) + a "Might be you?" strip (Probable).
 * Full links:  the whole gallery.
 * Both: collection tabs, infinite scroll, lightbox, downloads, WhatsApp share.
 */
@Component({
  selector: 'app-guest-gallery',
  imports: [MatButtonModule, MatChipsModule, MatIconModule, MatMenuModule, MatProgressBarModule, Lightbox, SelfieStep, NotifyCard],
  providers: [Realtime],
  templateUrl: './guest-gallery.html',
  styleUrl: './guest-gallery.scss',
})
export class GuestGallery {
  private readonly api = inject(GuestApi);
  private readonly notify = inject(Notify);
  private readonly realtime = inject(Realtime);
  private readonly guestSession = inject(GuestSession);
  /** Photos that arrived while the gallery is open (SignalR); shown as a tap-to-refresh banner. */
  protected readonly newPhotos = signal(0);

  readonly token = input.required<string>();
  readonly session = input.required<GuestSessionInfo>();
  /** Session ended (deleted data / link closed); carries a message for the landing page. */
  readonly signedOut = output<string>();

  /** Full and Selection links show the whole gallery (no selfie). */
  protected readonly isFull = computed(() => this.session().linkType === 'Full' || this.session().linkType === 'Selection');
  /** Selection link: the couple hearts photos for the album. */
  protected readonly isSelection = computed(() => this.session().linkType === 'Selection');
  protected readonly selection = signal<SelectionState | null>(null);
  /** photoId → comment, for quick lookups in the grid and lightbox. */
  protected readonly picks = computed(() => new Map((this.selection()?.items ?? []).map((i) => [i.photoId, i.comment] as const)));
  protected readonly collections = resource({ loader: () => this.api.collections() });
  protected readonly probable = resource({
    params: () => (this.isFull() ? undefined : true),
    loader: () => this.api.probable(),
  });

  protected readonly collectionId = signal<string | null>(null);
  protected readonly photos = signal<GuestPhoto[]>([]);
  protected readonly total = signal(0);
  protected readonly loading = signal(false);
  protected readonly refreshing = signal(false);
  protected readonly lightbox = signal<{ list: GuestPhoto[]; index: number; kind: 'mine' | 'maybe' | 'none' } | null>(null);
  protected readonly busy = signal(false);
  /** "Add another selfie" overlay. */
  protected readonly addingSelfie = signal(false);
  protected readonly selfieError = signal<string | null>(null);
  protected readonly selfieCount = signal(0);
  protected readonly canAddSelfie = computed(() => this.session().hasSelfie && this.selfieCount() < this.session().maxSelfies);
  private page = 0;
  private generation = 0;
  private readonly sentinel = viewChild<ElementRef<HTMLElement>>('sentinel');

  protected readonly totalAll = computed(() => (this.collections.value() ?? []).reduce((s, c) => s + c.photoCount, 0));
  protected readonly greeting = computed(() => {
    const name = this.session().guestName?.split(' ')[0];
    return name ? `Hi ${name}!` : 'Welcome!';
  });

  constructor() {
    effect(() => this.selfieCount.set(this.session().selfieCount));
    afterNextRender(() => {
      void this.goLive();
      if (this.isSelection()) this.api.selection().then((s) => this.selection.set(s), (err) => this.handle(err));
    });
    effect(() => {
      this.collectionId();
      untracked(() => void this.reset());
    });

    const destroyRef = inject(DestroyRef);
    afterNextRender(() => {
      const observer = new IntersectionObserver((entries) => {
        if (entries[0].isIntersecting && this.photos().length < this.total() && !this.loading()) void this.loadMore();
      }, { rootMargin: '600px' });
      const el = this.sentinel()?.nativeElement;
      if (el) observer.observe(el);
      destroyRef.onDestroy(() => observer.disconnect());
    });
  }

  // ---------- Selection link ----------

  /** Heart / un-heart. The server enforces the limit and the lock; we show its message (e.g. "40 of 40"). */
  protected async toggleSelect(photo: GuestPhoto, e?: Event): Promise<void> {
    e?.stopPropagation(); // the heart sits on top of the thumbnail: don't open the lightbox
    const s = this.selection();
    if (s?.locked) {
      this.notify.ok('Your selection was submitted. Ask the photographer if you need changes.');
      return;
    }
    try {
      this.selection.set(await (this.picks().has(photo.id) ? this.api.unselect(photo.id) : this.api.select(photo.id)));
    } catch (err) {
      this.handle(err);
    }
  }

  protected async editComment(photo: GuestPhoto): Promise<void> {
    const text = prompt('Note for the photographer (e.g. “crop tighter”, “black & white”):', this.picks().get(photo.id) ?? '');
    if (text === null) return;
    try {
      this.selection.set(await this.api.comment(photo.id, text.trim() || null));
    } catch (err) {
      this.handle(err);
    }
  }

  protected async submitSelection(): Promise<void> {
    const s = this.selection();
    if (!s || !confirm(`Send your ${s.count} photo${s.count === 1 ? '' : 's'} to the photographer? You can’t change them after this.`)) return;
    try {
      this.selection.set(await this.api.submitSelection());
      this.notify.ok('Sent! The photographer has your selection.');
    } catch (err) {
      this.handle(err);
    }
  }

  /** Live connection: Guest links hear "new photos of you", Full links hear "new photo in the gallery". */
  private async goLive(): Promise<void> {
    const token = this.guestSession.token();
    if (!token) return;
    await this.realtime.connect(token);
    this.realtime.on('NewMatchesForYou', (m) => this.newPhotos.update((n) => n + m.count));
    this.realtime.on('PhotoAdded', () => this.newPhotos.update((n) => n + 1));
  }

  /** Tap on the "N new photos" banner. */
  protected async showNew(): Promise<void> {
    this.newPhotos.set(0);
    this.collections.reload();
    this.probable.reload();
    this.collectionId.set(null);
    await this.reset();
    window.scrollTo({ top: 0, behavior: 'smooth' });
  }

  private async reset(): Promise<void> {
    this.generation++;
    this.page = 0;
    this.photos.set([]);
    this.total.set(0);
    await this.loadMore();
  }

  protected async loadMore(): Promise<void> {
    if (this.loading()) return;
    const gen = this.generation;
    this.loading.set(true);
    try {
      const r = await this.api.photos(this.isFull() ? 'gallery' : 'mine', this.collectionId(), this.page + 1, PAGE_SIZE);
      if (gen !== this.generation) return;
      this.page = r.page;
      this.total.set(r.total);
      this.photos.update((list) => [...list, ...r.items]);
    } catch (err) {
      this.handle(err);
    } finally {
      this.loading.set(false);
    }
  }

  protected open(list: GuestPhoto[], index: number, kind: 'mine' | 'maybe'): void {
    this.lightbox.set({ list, index, kind: this.isFull() ? 'none' : kind });
  }

  /** "Yes, that's me": moves the photo to "Your photos"; its face also helps "Find more". */
  protected async confirm(photo: GuestPhoto): Promise<void> {
    await this.decide(photo, true);
  }

  /** "Not me": the photo disappears for good. */
  protected async reject(photo: GuestPhoto): Promise<void> {
    await this.decide(photo, false);
  }

  private async decide(photo: GuestPhoto, isMe: boolean): Promise<void> {
    if (this.busy()) return;
    this.busy.set(true);
    try {
      await (isMe ? this.api.confirm(photo.id) : this.api.reject(photo.id));
      this.lightbox.set(null);
      if (!isMe) {
        this.photos.update((list) => list.filter((p) => p.id !== photo.id));
        if (this.photos().length < this.total()) this.total.update((t) => t - 1);
      }
      this.probable.reload();
      this.collections.reload();
      if (isMe) await this.reset();
      this.notify.ok(isMe
        ? 'Added to your photos. Tap “Find more photos of me” — we’ll use this photo to look for more.'
        : 'Removed. We won’t show it to you again.');
    } catch (err) {
      this.handle(err);
    } finally {
      this.busy.set(false);
    }
  }

  /**
   * "Find more photos of me": search again with all the guest's selfies and confirmed faces.
   * (New uploads already arrive by themselves; this helps after "That's me" or another selfie.)
   */
  protected async refresh(): Promise<void> {
    this.refreshing.set(true);
    const before = this.total();
    try {
      await this.api.findMore();
      this.collections.reload();
      this.probable.reload();
      await this.reset();
      const found = this.total() - before;
      this.notify.ok(found > 0
        ? `Found  more photo of you!`
        : 'Nothing new yet. Tip: say “That’s me” on photos under “Might be you?”, or add another selfie.');
    } catch (err) {
      this.handle(err);
    } finally {
      this.refreshing.set(false);
    }
  }

  /** Extra selfie from the overlay. 422 = not usable: show the reason and let them retake. */
  protected async uploadSelfie(blob: Blob): Promise<void> {
    this.busy.set(true);
    this.selfieError.set(null);
    const before = this.total();
    try {
      await this.api.addSelfie(blob);
      this.selfieCount.update((n) => n + 1);
      this.addingSelfie.set(false);
      this.collections.reload();
      this.probable.reload();
      await this.reset();
      const found = this.total() - before;
      this.notify.ok(found > 0 ? `Thanks! That found  more photo of you.` : 'Thanks! We’ll use it for new photos too.');
    } catch (err) {
      if (err instanceof HttpErrorResponse && (err.status === 422 || err.status === 400)) this.selfieError.set(errorMessage(err));
      else this.handle(err);
    } finally {
      this.busy.set(false);
    }
  }

  protected async download(photo: GuestPhoto): Promise<void> {
    try {
      // The API checks permission and returns a short-lived signed link; the browser then downloads it.
      window.location.href = await this.api.downloadUrl(photo.id);
    } catch (err) {
      this.handle(err);
    }
  }

  protected async downloadAll(): Promise<void> {
    try {
      window.location.href = await this.api.downloadAllUrl(this.collectionId());
      this.notify.ok('Your ZIP download is starting…');
    } catch (err) {
      this.handle(err);
    }
  }

  protected whatsapp(): string {
    const url = `${location.origin}/g/${this.token()}`;
    const text = this.isFull()
      ? `Photos from ${this.session().event.name}: ${url}`
      : `Find your photos from ${this.session().event.name}! Take a selfie and see every photo you're in: ${url}`;
    return `https://wa.me/?text=${encodeURIComponent(text)}`;
  }

  protected async deleteMyData(): Promise<void> {
    if (!confirm('Delete your selfie and details? You will no longer see your photos unless you take a new selfie.')) return;
    try {
      await this.api.deleteMe();
      this.signedOut.emit('Your selfie and details were deleted.');
    } catch (err) {
      this.handle(err);
    }
  }

  protected signOut(): void {
    this.signedOut.emit('');
  }

  private handle(err: unknown): void {
    const status = (err as { status?: number }).status;
    if (status === 401 || status === 403) {
      this.signedOut.emit('This gallery isn’t available right now. Please open the link again.');
      return;
    }
    this.notify.error(err);
  }
}
