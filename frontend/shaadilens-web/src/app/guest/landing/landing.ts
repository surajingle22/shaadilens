import { DatePipe } from '@angular/common';
import { Component, input, output } from '@angular/core';
import { MatButtonModule } from '@angular/material/button';
import { MatIconModule } from '@angular/material/icon';
import { PublicLink } from '../../core/api/guest-api';

@Component({
  selector: 'app-landing',
  imports: [DatePipe, MatButtonModule, MatIconModule],
  template: `
    <main class="landing">
      <div class="cover" [style.background-image]="link().event.coverUrl ? 'url(' + link().event.coverUrl + ')' : null">
        @if (!link().event.coverUrl) { <mat-icon>favorite</mat-icon> }
      </div>
      <div class="text">
        <h1>{{ link().event.name }}</h1>
        <p class="meta">
          @if (link().event.date) { {{ link().event.date | date: 'd MMMM y' }} }
          @if (link().event.date && link().event.venue) { · }
          {{ link().event.venue }}
        </p>

        @if (notice()) { <p class="notice" role="status">{{ notice() }}</p> }

        @if (link().selfieRequired) {
          <p class="lead">Take one selfie and we’ll find every photo you’re in.</p>
          <button mat-flat-button class="cta" (click)="start.emit()"><mat-icon>photo_camera</mat-icon> Find your photos</button>
          <ol class="how">
            <li><mat-icon>badge</mat-icon> Tell us your name</li>
            <li><mat-icon>face</mat-icon> Take a quick selfie</li>
            <li><mat-icon>photo_library</mat-icon> See &amp; download your photos</li>
          </ol>
          <p class="privacy"><mat-icon>lock</mat-icon> Your selfie is used only to find your photos at this event. You can delete it anytime.</p>
        } @else {
          <p class="lead">All the photos from the celebration, in one place.</p>
          <button mat-flat-button class="cta" (click)="start.emit()"><mat-icon>photo_library</mat-icon> View photos</button>
        }
      </div>
    </main>
  `,
  styles: `
    .landing { min-height: 100dvh; display: flex; flex-direction: column; max-width: 560px; margin: 0 auto; }
    .cover { height: 42dvh; min-height: 220px; background: var(--mat-sys-primary-container) center / cover no-repeat;
      display: grid; place-items: center; border-radius: 0 0 28px 28px; }
    .cover mat-icon { font-size: 72px; width: 72px; height: 72px; color: var(--mat-sys-primary); }
    .text { padding: 24px 20px 32px; text-align: center; display: flex; flex-direction: column; align-items: center; gap: 8px; }
    h1 { font: var(--mat-sys-headline-medium); font-family: Georgia, 'Times New Roman', serif; color: var(--mat-sys-primary); margin: 0; }
    .meta { color: var(--mat-sys-on-surface-variant); margin: 0; }
    .lead { margin: 12px 0 4px; font: var(--mat-sys-body-large); }
    .cta { height: 52px; padding: 0 28px; font-size: 17px; border-radius: 26px; margin: 8px 0; }
    .how { list-style: none; padding: 0; margin: 12px 0 0; display: flex; gap: 12px; justify-content: center; flex-wrap: wrap; }
    .how li { display: flex; flex-direction: column; align-items: center; gap: 4px; width: 92px; font: var(--mat-sys-body-small); }
    .how mat-icon { color: var(--mat-sys-primary); }
    .privacy { display: flex; gap: 6px; align-items: flex-start; text-align: left; font: var(--mat-sys-body-small);
      color: var(--mat-sys-on-surface-variant); margin-top: 20px; max-width: 360px; }
    .privacy mat-icon { font-size: 16px; width: 16px; height: 16px; flex-shrink: 0; margin-top: 2px; }
    .notice { background: var(--mat-sys-error-container); color: var(--mat-sys-on-error-container); padding: 8px 12px; border-radius: 8px; }
  `,
})
export class Landing {
  readonly link = input.required<PublicLink>();
  readonly notice = input<string | null>(null);
  readonly start = output<void>();
}
