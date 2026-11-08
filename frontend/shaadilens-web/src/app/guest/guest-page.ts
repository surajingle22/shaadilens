import { HttpErrorResponse } from '@angular/common/http';
import { Component, computed, effect, inject, input, signal, untracked } from '@angular/core';
import { MatButtonModule } from '@angular/material/button';
import { MatIconModule } from '@angular/material/icon';
import { MatProgressSpinnerModule } from '@angular/material/progress-spinner';
import { Title } from '@angular/platform-browser';
import { GuestApi, GuestSessionInfo, PublicLink } from '../core/api/guest-api';
import { ProblemDetails } from '../core/api/api.models';
import { GuestSession } from '../core/guest/guest-session';
import { errorMessage } from '../core/notify';
import { GuestGallery } from './gallery/guest-gallery';
import { Landing } from './landing/landing';
import { PinStep } from './steps/pin-step';
import { RegisterDetails, RegisterStep } from './steps/register-step';
import { SelfieStep } from './steps/selfie-step';

type Step = 'loading' | 'landing' | 'pin' | 'details' | 'selfie' | 'matching' | 'gallery' | 'closed';

/**
 * The guest flow for one share link, as a small state machine:
 *
 *   loading ─┬─ (saved session still valid) ──────────────────────────────► gallery
 *            └─ landing → [pin] → [details + consent] → [selfie] → matching → gallery
 *                                                     ▲            │ 422 "Move closer"
 *                                                     └────────────┘
 */
@Component({
  selector: 'app-guest-page',
  imports: [MatButtonModule, MatIconModule, MatProgressSpinnerModule, Landing, PinStep, RegisterStep, SelfieStep, GuestGallery],
  template: `
    <div class="guest" [style.--mat-sys-primary]="theme()" [style.--mat-sys-primary-container]="themeSoft()" [style.--title-font]="titleFontCss()">
      @switch (step()) {
        @case ('loading') {
          <div class="center"><mat-spinner diameter="40" /></div>
        }
        @case ('closed') {
          <div class="center message">
            <mat-icon>event_busy</mat-icon>
            <p>{{ closedMessage() }}</p>
          </div>
        }
        @case ('landing') {
          <app-landing [link]="link()!" [notice]="notice()" (start)="next('landing')" />
        }
        @case ('pin') {
          <app-pin-step [token]="token()" (verified)="pinTicket.set($event); next('pin')" />
        }
        @case ('details') {
          <app-register-step [link]="link()!" [initial]="details()" (submitted)="details.set($event); next('details')" />
        }
        @case ('selfie') {
          <app-selfie-step [error]="selfieError()" (captured)="selfie.set($event); next('selfie')" (back)="back()" />
        }
        @case ('matching') {
          <div class="center message">
            <mat-spinner diameter="48" />
            <p><strong>{{ link()?.selfieRequired ? 'Matching your face…' : 'Opening the gallery…' }}</strong></p>
            @if (link()?.selfieRequired) {
              <p class="muted">We’re looking through every photo for you. This takes a few seconds.</p>
            }
          </div>
        }
        @case ('gallery') {
          <app-guest-gallery [token]="token()" [session]="session()!" (signedOut)="signedOut($event)" />
        }
      }
    </div>
  `,
  styles: `
    .guest { min-height: 100dvh; background: var(--mat-sys-surface); }
    .center { min-height: 100dvh; display: flex; flex-direction: column; align-items: center; justify-content: center; gap: 12px; padding: 24px; text-align: center; }
    .message mat-icon { font-size: 48px; width: 48px; height: 48px; color: var(--mat-sys-on-surface-variant); }
    .message p { max-width: 320px; margin: 0; }
    .muted { color: var(--mat-sys-on-surface-variant); }
  `,
})
export class GuestPage {
  private readonly api = inject(GuestApi);
  private readonly saved = inject(GuestSession);
  private readonly title = inject(Title);

  /** From the route: /g/:token */
  readonly token = input.required<string>();

  protected readonly step = signal<Step>('loading');
  protected readonly link = signal<PublicLink | null>(null);
  protected readonly session = signal<GuestSessionInfo | null>(null);
  protected readonly pinTicket = signal<string | null>(null);
  protected readonly details = signal<RegisterDetails | null>(null);
  protected readonly selfie = signal<Blob | null>(null);
  protected readonly selfieError = signal<string | null>(null);
  protected readonly closedMessage = signal('This gallery isn’t open right now.');
  protected readonly notice = signal<string | null>(null);

  protected readonly theme = computed(() => this.link()?.event.themeColor ?? this.session()?.event.themeColor ?? null);
  /** A very light tint of the theme colour for containers. */
  protected readonly themeSoft = computed(() => (this.theme() ? `color-mix(in srgb, ${this.theme()} 14%, white)` : null));

  /** The couple's title font (from a fixed, server-validated list), loaded from Google Fonts on demand. */
  private readonly titleFont = computed(() => this.link()?.event.titleFont ?? this.session()?.event.titleFont ?? null);
  protected readonly titleFontCss = computed(() => (this.titleFont() ? `'${this.titleFont()}', Georgia, serif` : null));

  constructor() {
    effect(() => {
      const font = this.titleFont();
      if (!font || document.querySelector(`link[data-title-font="${font}"]`)) return;
      const css = document.createElement('link');
      css.rel = 'stylesheet';
      css.href = `https://fonts.googleapis.com/css2?family=${encodeURIComponent(font)}&display=swap`;
      css.dataset['titleFont'] = font;
      document.head.appendChild(css);
    });
    effect(() => {
      const token = this.token();
      untracked(() => void this.start(token));
    });
  }

  private async start(token: string): Promise<void> {
    this.step.set('loading');
    // Returning guest? Try the saved session first — no selfie needed again.
    if (this.saved.open(token)) {
      try {
        this.session.set(await this.api.me());
        this.link.set(await this.api.link(token).catch(() => null));
        this.title.setTitle(this.session()!.event.name);
        this.step.set('gallery');
        return;
      } catch {
        this.saved.forget(token);
      }
    }
    try {
      const link = await this.api.link(token);
      this.link.set(link);
      this.title.setTitle(link.event.name);
      this.step.set('landing');
    } catch (err) {
      this.closedMessage.set(err instanceof HttpErrorResponse && err.status === 404
        ? 'This link doesn’t exist. Please scan the QR code again.'
        : errorMessage(err, 'This gallery isn’t open right now.'));
      this.step.set('closed');
    }
  }

  /** Decide the next step after `from` finished. */
  protected next(from: Step): void {
    const link = this.link()!;
    const needsForm = (link.requireRegistration && link.registrationFields !== 'None') || link.selfieRequired;
    const order: Step[] = ['landing', 'pin', 'details', 'selfie', 'matching'];
    for (const candidate of order.slice(order.indexOf(from) + 1)) {
      if (candidate === 'pin' && !link.requiresPin) continue;
      if (candidate === 'details' && !needsForm) continue;
      if (candidate === 'selfie' && !link.selfieRequired) continue;
      if (candidate === 'matching') {
        void this.register();
        return;
      }
      this.step.set(candidate);
      return;
    }
  }

  protected back(): void {
    this.step.set(this.link()!.requireRegistration || this.link()!.selfieRequired ? 'details' : 'landing');
  }

  private async register(): Promise<void> {
    this.step.set('matching');
    this.selfieError.set(null);
    const d = this.details();
    try {
      const result = await this.api.register(this.token(), {
        name: d?.name, phone: d?.phone, email: d?.email, consent: d?.consent ?? false,
        pinTicket: this.pinTicket(), selfie: this.selfie(),
      });
      this.saved.save(this.token(), result.token, result.expiresAt);
      this.session.set(result.session);
      this.step.set('gallery');
    } catch (err) {
      const problem = err instanceof HttpErrorResponse ? (err.error as ProblemDetails | null) : null;
      if (err instanceof HttpErrorResponse && err.status === 422) {
        // Selfie not usable: show the reason ("Move closer", "Look straight…") and let them retake it.
        this.selfieError.set(problem?.detail ?? 'Please try another selfie.');
        this.step.set('selfie');
      } else if (err instanceof HttpErrorResponse && err.status === 403 && this.link()!.requiresPin) {
        this.pinTicket.set(null);
        this.step.set('pin');
      } else if (err instanceof HttpErrorResponse && err.status === 400) {
        this.notice.set(errorMessage(err));
        this.step.set(this.link()!.selfieRequired || this.link()!.requireRegistration ? 'details' : 'landing');
      } else {
        this.notice.set(errorMessage(err));
        this.step.set('landing');
      }
    }
  }

  /** From the gallery: "Delete my data" or a closed session. */
  protected signedOut(message: string): void {
    this.saved.forget(this.token());
    this.session.set(null);
    this.selfie.set(null);
    this.details.set(null);
    this.notice.set(message);
    void this.start(this.token()).then(() => this.notice.set(message));
  }
}
