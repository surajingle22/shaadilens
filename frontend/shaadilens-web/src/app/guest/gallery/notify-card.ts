import { Component, computed, inject, input, signal } from '@angular/core';
import { MatButtonModule } from '@angular/material/button';
import { MatCheckboxModule } from '@angular/material/checkbox';
import { MatIconModule } from '@angular/material/icon';
import { SwPush } from '@angular/service-worker';
import { GuestApi, NotificationStatus } from '../../core/api/guest-api';
import { Notify } from '../../core/notify';

/**
 * "Notify me when new photos of me arrive".
 *
 * Web Push: the browser asks "Allow notifications?", then gives us a subscription (an https endpoint at
 * Google/Mozilla/Apple + encryption keys). We send it to our API; when new photos match, the server sends
 * one message there and the phone shows it — even with the page closed.
 * iPhone: Web Push only works after "Add to Home Screen" (iOS 16.4+), so we explain that.
 * Email is offered when the guest gave an address and the server has SMTP set up.
 */
@Component({
  selector: 'app-notify-card',
  imports: [MatButtonModule, MatCheckboxModule, MatIconModule],
  template: `
    @if (available()) {
      <section class="card" [class.on]="isOn()">
        @if (isOn()) {
          <mat-icon>notifications_active</mat-icon>
          <p><strong>We’ll let you know</strong> when new photos of you arrive{{ status().emailOn && !status().pushOn ? ' (by email)' : '' }}.</p>
          <button mat-button (click)="off()" [disabled]="busy()">Turn off</button>
        } @else {
          <mat-icon>notifications</mat-icon>
          <div class="text">
            <p><strong>More photos are coming.</strong> Get a message when new photos of you arrive.</p>
            @if (status().emailAvailable) {
              <mat-checkbox [checked]="email()" (change)="email.set($event.checked)">Also email me</mat-checkbox>
            }
            @if (iosNeedsInstall()) {
              <p class="hint">On iPhone: tap <mat-icon inline>ios_share</mat-icon> Share → <b>Add to Home Screen</b>, open it from there, then tap “Notify me”.</p>
            }
          </div>
          <button mat-flat-button (click)="on()" [disabled]="busy()">Notify me</button>
        }
      </section>
    }
  `,
  styles: `
    .card { display: flex; align-items: center; gap: 12px; margin: 0 16px 12px; padding: 12px 14px; border-radius: 16px;
      background: var(--mat-sys-primary-container); color: var(--mat-sys-on-primary-container); flex-wrap: wrap; }
    .card.on { background: var(--mat-sys-surface-container); color: var(--mat-sys-on-surface); }
    .card > mat-icon { flex: none; }
    .card p { margin: 0; }
    .text, .card.on p { flex: 1; min-width: 180px; }
    .hint { font: var(--mat-sys-body-small); margin-top: 6px !important; }
    button[mat-flat-button] { border-radius: 20px; }
  `,
})
export class NotifyCard {
  private readonly api = inject(GuestApi);
  private readonly swPush = inject(SwPush);
  private readonly notify = inject(Notify);

  readonly initial = input.required<NotificationStatus>();
  private readonly changed = signal<NotificationStatus | null>(null);
  protected readonly status = computed(() => this.changed() ?? this.initial());
  protected readonly email = signal(true);
  protected readonly busy = signal(false);

  private readonly pushPossible = computed(() => !!this.status().pushPublicKey && this.swPush.isEnabled && 'PushManager' in window);
  protected readonly isOn = computed(() => this.status().pushOn || this.status().emailOn);
  /** Show the card only if at least one channel can work on this server. */
  protected readonly available = computed(() => !!this.status().pushPublicKey || this.status().emailAvailable);
  protected readonly iosNeedsInstall = computed(() =>
    !!this.status().pushPublicKey && !this.pushPossible() && /iPhone|iPad|iPod/.test(navigator.userAgent) &&
    !(navigator as Navigator & { standalone?: boolean }).standalone);

  protected async on(): Promise<void> {
    this.busy.set(true);
    try {
      let subscription: PushSubscriptionJSON | null = null;
      if (this.pushPossible()) {
        try {
          const sub = await this.swPush.requestSubscription({ serverPublicKey: this.status().pushPublicKey! });
          subscription = sub.toJSON();
        } catch {
          if (Notification.permission === 'denied')
            this.notify.ok('Notifications are blocked for this site. You can allow them in your browser settings.');
        }
      }
      const email = this.status().emailAvailable && this.email();
      if (!subscription && !email) {
        if (!this.iosNeedsInstall() && !this.pushPossible()) this.notify.ok('This browser can’t show notifications. Just come back to this link later!');
        return;
      }
      this.changed.set(await this.api.setNotifications(subscription, email));
      this.notify.ok('Done! We’ll tell you when new photos of you arrive.');
    } catch (err) {
      this.notify.error(err);
    } finally {
      this.busy.set(false);
    }
  }

  protected async off(): Promise<void> {
    this.busy.set(true);
    try {
      await this.swPush.unsubscribe().catch(() => undefined);
      this.changed.set(await this.api.notificationsOff());
    } catch (err) {
      this.notify.error(err);
    } finally {
      this.busy.set(false);
    }
  }
}
