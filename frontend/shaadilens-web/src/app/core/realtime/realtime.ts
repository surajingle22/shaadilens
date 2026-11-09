import { DestroyRef, Injectable, inject, signal } from '@angular/core';
import type { ReelStatus } from '../api/guest-api';
import { HubConnection, HubConnectionBuilder, HubConnectionState, LogLevel } from '@microsoft/signalr';

/** Messages the server pushes (see EventsHub.cs). */
export interface HubMessages {
  /** Admin: a photo finished a pipeline step. */
  PhotoProcessed: { eventId: string; photoId: string; status: string };
  /** Full/Selection gallery + TV: a new photo is ready to show. */
  PhotoAdded: { photoId: string };
  /** Guest: new photos of you just arrived. */
  NewMatchesForYou: { count: number };
  /** Guest: your highlight reel changed state (Rendering → Ready / Failed). */
  ReelUpdated: { reelId: string; status: ReelStatus };
}

/**
 * One SignalR connection (a WebSocket to /hubs/events) for the component that injects this service.
 * Provide it in the component (`providers: [Realtime]`) so it disconnects when the component is destroyed.
 *
 *   admin:  realtime.connect();            then realtime.invoke('WatchEvent', id)  (the admin cookie authenticates)
 *   guest:  realtime.connect(guestJwt)                                             (joins the guest's group by itself)
 *
 * Reconnects automatically (Wi-Fi at weddings is flaky); `connected` is a signal for "live" indicators.
 */
@Injectable()
export class Realtime {
  private hub: HubConnection | null = null;
  private readonly onReconnected: (() => void)[] = [];
  readonly connected = signal(false);

  constructor() {
    inject(DestroyRef).onDestroy(() => void this.hub?.stop());
  }

  async connect(accessToken?: string): Promise<void> {
    if (this.hub) return;
    this.hub = new HubConnectionBuilder()
      .withUrl('/hubs/events', accessToken ? { accessTokenFactory: () => accessToken } : {})
      .withAutomaticReconnect([0, 2000, 5000, 10000, 30000])
      .configureLogging(LogLevel.Warning)
      .build();
    this.hub.onreconnecting(() => this.connected.set(false));
    this.hub.onreconnected(() => {
      this.connected.set(true);
      this.onReconnected.forEach((fn) => fn()); // groups are per connection: re-join after a reconnect
    });
    this.hub.onclose(() => this.connected.set(false));
    try {
      await this.hub.start();
      this.connected.set(true);
    } catch {
      // Live updates are optional — pages still work (and poll) without them.
      this.connected.set(false);
    }
  }

  on<K extends keyof HubMessages>(name: K, handler: (msg: HubMessages[K]) => void): void {
    this.hub?.on(name, handler);
  }

  /** Call a hub method now and again after every reconnect (e.g. WatchEvent). */
  async invokeAndKeep(method: string, ...args: unknown[]): Promise<void> {
    const call = () => {
      if (this.hub?.state === HubConnectionState.Connected) void this.hub.invoke(method, ...args).catch(() => undefined);
    };
    this.onReconnected.push(call);
    call();
  }
}
