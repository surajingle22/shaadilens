import { Component, inject, resource, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { MatButtonModule } from '@angular/material/button';
import { MatFormFieldModule } from '@angular/material/form-field';
import { MatIconModule } from '@angular/material/icon';
import { MatInputModule } from '@angular/material/input';
import { MatProgressBarModule } from '@angular/material/progress-bar';
import { AdminApi } from '../../core/api/admin-api';
import { VapidKeys } from '../../core/api/api.models';
import { Notify } from '../../core/notify';

/**
 * Settings: what's configured, plus two helpers. Nothing secret is edited or stored here — passwords and
 * keys live in .env (restart the api after changing it: `docker compose up -d api`).
 */
@Component({
  selector: 'app-settings-page',
  imports: [FormsModule, MatButtonModule, MatFormFieldModule, MatIconModule, MatInputModule, MatProgressBarModule],
  template: `
    <h1>Settings</h1>
    <p class="muted">Values come from <code>.env</code>. Change them there, then run <code>docker compose up -d api</code>.</p>
    @if (settings.isLoading() && !settings.hasValue()) { <mat-progress-bar mode="indeterminate" /> }

    @if (settings.value(); as s) {
      <section class="panel">
        <h2>General</h2>
        <dl>
          <dt>Public address (QR codes, links)</dt><dd><code>{{ s.publicBaseUrl }}</code></dd>
          <dt>Photo storage</dt><dd><code>{{ s.storageRoot }}</code></dd>
          <dt>Face service</dt><dd><code>{{ s.faceServiceUrl }}</code></dd>
          <dt>Workers</dt><dd>{{ s.faceWorkers }} face · {{ s.imageWorkers }} image <span class="muted">(FACE_WORKERS)</span></dd>
        </dl>
      </section>

      <section class="panel">
        <h2><mat-icon>movie</mat-icon> Highlight reels</h2>
        @if (s.reels.workerOnline) {
          <p class="ok"><mat-icon>check_circle</mat-icon> Reel worker is running.</p>
        } @else {
          <p class="off"><mat-icon>error_outline</mat-icon> Reel worker isn’t running — reels stay “Making your reel…”. Start it: <code>docker compose up -d reel-worker</code></p>
        }
        @if (s.reels.tracks.length) {
          <p>{{ s.reels.tracks.length }} music track(s) in <code>./music</code> (one is picked at random per reel):</p>
          <ul class="tracks">@for (t of s.reels.tracks; track t) { <li>{{ t }}</li> }</ul>
        } @else {
          <p class="off"><mat-icon>music_off</mat-icon> No music yet — reels are silent. Put royalty-free .mp3 files in the <code>music</code> folder next to docker-compose.yml.</p>
        }
        <p class="small muted">Turn reels on/off and choose the number of photos per event on its Overview tab. CPU limit: <code>REEL_CPUS</code>.</p>
      </section>

      <section class="panel">
        <h2><mat-icon>mail</mat-icon> Email</h2>
        @if (s.smtp.configured) {
          <p class="ok"><mat-icon>check_circle</mat-icon> Sending via <code>{{ s.smtp.host }}:{{ s.smtp.port }}</code> as <code>{{ s.smtp.from }}</code></p>
          <div class="inline">
            <mat-form-field appearance="outline">
              <mat-label>Send a test email to</mat-label>
              <input matInput type="email" [ngModel]="testTo()" (ngModelChange)="testTo.set($event)" />
            </mat-form-field>
            <button mat-stroked-button (click)="sendTest()" [disabled]="busy() || !testTo()">Send test</button>
          </div>
        } @else {
          <p class="off"><mat-icon>info</mat-icon> Off. Optional — guests can still get Web Push.</p>
          <p class="small">Gmail: set <code>SMTP_HOST=smtp.gmail.com</code>, <code>SMTP_USER</code> = your address,
            <code>SMTP_PASS</code> = an <b>App Password</b> (Google Account → Security → App passwords), <code>SMTP_FROM</code> = your address.</p>
        }
      </section>

      <section class="panel">
        <h2><mat-icon>notifications</mat-icon> Web Push (phone notifications)</h2>
        @if (s.push.configured) {
          <p class="ok"><mat-icon>check_circle</mat-icon> On. Guests see a “Notify me” button in their gallery.</p>
        } @else {
          <p class="off"><mat-icon>info</mat-icon> Off. Generate a key pair, paste both lines into <code>.env</code>, restart the api.</p>
        }
        <p class="small muted">Push needs HTTPS (Cloudflare Tunnel) on phones. iPhones: only after “Add to Home Screen”.</p>
        <button mat-stroked-button (click)="generate()" [disabled]="busy()"><mat-icon>key</mat-icon> Generate VAPID keys</button>
        @if (keys(); as k) {
          <pre class="keys">{{ k.envLines }}</pre>
          <button mat-button (click)="copy(k)"><mat-icon>content_copy</mat-icon> Copy</button>
          <p class="small muted">Keep the private key secret. Changing keys later turns off existing subscriptions (guests tap “Notify me” again).</p>
        }
      </section>
    }
  `,
  styles: `
    :host { display: block; max-width: 860px; margin: 0 auto; padding: 16px; }
    h1 { margin: 8px 0 4px; }
    .panel h2 { display: flex; align-items: center; gap: 8px; }
    dl { display: grid; grid-template-columns: minmax(160px, max-content) 1fr; gap: 6px 16px; margin: 0; }
    dt { color: var(--mat-sys-on-surface-variant); }
    dd { margin: 0; overflow-wrap: anywhere; }
    .ok, .off { display: flex; align-items: center; gap: 6px; }
    .ok mat-icon { color: #2e7d32; }
    .off mat-icon { color: var(--mat-sys-on-surface-variant); }
    .inline { display: flex; gap: 12px; align-items: baseline; flex-wrap: wrap; }
    .keys { background: var(--mat-sys-surface-container); padding: 12px; border-radius: 8px; overflow-x: auto; font-size: 12px; }
    section { margin-bottom: 16px; }
    .tracks { margin: 4px 0 8px; padding-left: 20px; columns: 2; font-size: 14px; }
  `,
})
export class SettingsPage {
  private readonly api = inject(AdminApi);
  private readonly notify = inject(Notify);

  protected readonly settings = resource({ loader: () => this.api.settings() });
  protected readonly testTo = signal('');
  protected readonly keys = signal<VapidKeys | null>(null);
  protected readonly busy = signal(false);

  protected async sendTest(): Promise<void> {
    this.busy.set(true);
    try {
      await this.api.testEmail(this.testTo());
      this.notify.ok('Sent — check the inbox (and spam folder).');
    } catch (err) {
      this.notify.error(err);
    } finally {
      this.busy.set(false);
    }
  }

  protected async generate(): Promise<void> {
    this.busy.set(true);
    try {
      this.keys.set(await this.api.vapidKeys());
    } catch (err) {
      this.notify.error(err);
    } finally {
      this.busy.set(false);
    }
  }

  protected async copy(k: VapidKeys): Promise<void> {
    await navigator.clipboard.writeText(k.envLines).then(
      () => this.notify.ok('Copied. Paste into .env, then: docker compose up -d api'),
      () => this.notify.ok('Select the text and copy it manually.'),
    );
  }
}
