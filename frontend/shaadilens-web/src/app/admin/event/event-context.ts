import { Injectable, inject, resource, signal } from '@angular/core';
import { AdminApi } from '../../core/api/admin-api';

/**
 * The event currently open in the admin, shared by all its tabs (overview, upload, photos, share, guests).
 * Provided by EventShell, so each open event gets its own instance.
 */
@Injectable()
export class EventContext {
  private readonly api = inject(AdminApi);

  readonly eventId = signal<string | undefined>(undefined);

  /** Re-runs automatically when eventId changes. */
  readonly event = resource({
    params: () => this.eventId(),
    loader: ({ params: id }) => this.api.getEvent(id),
  });

  readonly stats = resource({
    params: () => this.eventId(),
    loader: ({ params: id }) => this.api.stats(id),
  });

  reload(): void {
    this.event.reload();
    this.stats.reload();
  }
}
