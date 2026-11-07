import { Component, DestroyRef, ElementRef, afterNextRender, computed, effect, inject, input, output, resource, signal, untracked, viewChild } from '@angular/core';
import { MatButtonModule } from '@angular/material/button';
import { MatChipsModule } from '@angular/material/chips';
import { MatIconModule } from '@angular/material/icon';
import { MatMenuModule } from '@angular/material/menu';
import { MatProgressBarModule } from '@angular/material/progress-bar';
import { GuestApi, GuestPhoto, GuestSessionInfo } from '../../core/api/guest-api';
import { Notify } from '../../core/notify';
import { Lightbox } from './lightbox';

const PAGE_SIZE = 60;

/**
 * Guest links: "Your photos" (Auto/Confirmed) + a "Might be you?" strip (Probable).
 * Full links:  the whole gallery.
 * Both: collection tabs, infinite scroll, lightbox, downloads, WhatsApp share.
 */
@Component({
  selector: 'app-guest-gallery',
  imports: [MatButtonModule, MatChipsModule, MatIconModule, MatMenuModule, MatProgressBarModule, Lightbox],
  templateUrl: './guest-gallery.html',
  styleUrl: './guest-gallery.scss',
})
export class GuestGallery {
  private readonly api = inject(GuestApi);
  private readonly notify = inject(Notify);

  readonly token = input.required<string>();
  readonly session = input.required<GuestSessionInfo>();
  /** Session ended (deleted data / link closed); carries a message for the landing page. */
  readonly signedOut = output<string>();

  protected readonly isFull = computed(() => this.session().linkType === 'Full');
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
  protected readonly lightbox = signal<{ list: GuestPhoto[]; index: number } | null>(null);
  private page = 0;
  private generation = 0;
  private readonly sentinel = viewChild<ElementRef<HTMLElement>>('sentinel');

  protected readonly totalAll = computed(() => (this.collections.value() ?? []).reduce((s, c) => s + c.photoCount, 0));
  protected readonly greeting = computed(() => {
    const name = this.session().guestName?.split(' ')[0];
    return name ? `Hi ${name}!` : 'Welcome!';
  });

  constructor() {
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

  protected open(list: GuestPhoto[], index: number): void {
    this.lightbox.set({ list, index });
  }

  /** "Check for new photos": search all faces again (photos uploaded after you registered). */
  protected async refresh(): Promise<void> {
    this.refreshing.set(true);
    const before = this.total();
    try {
      await this.api.refresh();
      this.collections.reload();
      this.probable.reload();
      await this.reset();
      const found = this.total() - before;
      this.notify.ok(found > 0 ? `Found ${found} new photo${found > 1 ? 's' : ''} of you!` : 'No new photos yet — check again later.');
    } catch (err) {
      this.handle(err);
    } finally {
      this.refreshing.set(false);
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
