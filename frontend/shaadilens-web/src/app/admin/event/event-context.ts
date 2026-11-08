import { DestroyRef, Injectable, effect, inject, resource, signal, untracked } from '@angular/core';
import { AdminApi } from '../../core/api/admin-api';
import { Realtime } from '../../core/realtime/realtime';

/**
 * The event currently open in the admin, shared by all its tabs (overview, upload, photos, share, guests).
 * Provided by EventShell, so each open event gets its own instance.
 *
 * Live updates: watches the event over SignalR. Every "PhotoProcessed" bumps `photoTick` and (debounced)
 * reloads the stats, so the progress bar and counters move while photos are processed — no polling needed.
 */
@Injectable()
export class EventContext {
  private readonly api = inject(AdminApi);
  private readonly realtime = inject(Realtime);

  readonly eventId = signal<string | undefined>(undefined);
  /** Increments on every processed photo (pages can react, e.g. refresh their grid). */
  readonly photoTick = signal(0);
  /** True while the live connection is up (otherwise pages fall back to polling). */
  readonly live = this.realtime.connected;

  /** Re-runs automatically when eventId changes. */
  readonly event = resource({
    params: () => this.eventId(),
    loader: ({ params: id }) => this.api.getEvent(id),
  });

  readonly stats = resource({
    params: () => this.eventId(),
    loader: ({ params: id }) => this.api.stats(id),
  });

  private statsTimer: ReturnType<typeof setTimeout> | undefined;

  constructor() {
    effect(() => {
      const id = this.eventId();
      if (id) untracked(() => void this.watch(id));
    });
    inject(DestroyRef).onDestroy(() => clearTimeout(this.statsTimer));
  }

  private async watch(eventId: string): Promise<void> {
    await this.realtime.connect(); // admin cookie authenticates
    this.realtime.on('PhotoProcessed', (m) => {
      if (m.eventId !== this.eventId()) return;
      this.photoTick.update((n) => n + 1);
      // Hundreds of photos per minute → at most one stats request per second.
      clearTimeout(this.statsTimer);
      this.statsTimer = setTimeout(() => this.stats.reload(), 1000);
    });
    await this.realtime.invokeAndKeep('WatchEvent', eventId);
  }

  reload(): void {
    this.event.reload();
    this.stats.reload();
  }
}
