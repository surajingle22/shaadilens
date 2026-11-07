import { Component, DestroyRef, ElementRef, afterNextRender, inject, input, output, signal, viewChild } from '@angular/core';
import { MatButtonModule } from '@angular/material/button';
import { MatIconModule } from '@angular/material/icon';

/**
 * Selfie capture.
 *  - Live front camera (getUserMedia) with an oval guide. Works on HTTPS (Cloudflare Tunnel) and on localhost.
 *  - Fallback: the phone's own camera app via <input type="file" capture="user"> — always works, even on plain HTTP.
 * The server checks the selfie (one face, close enough, frontal, sharp) and we show its message if it's not usable.
 */
@Component({
  selector: 'app-selfie-step',
  imports: [MatButtonModule, MatIconModule],
  template: `
    <main class="step">
      <h1>Take a selfie</h1>
      <p class="muted">Face the light, look straight at the camera, and fit your face in the oval.</p>

      @if (error()) {
        <p class="error" role="alert"><mat-icon>info</mat-icon> {{ error() }}</p>
      }

      <div class="viewer">
        @if (preview()) {
          <img [src]="preview()" alt="Your selfie" />
        } @else {
          <video #video autoplay playsinline muted [class.hidden]="!cameraOn()"></video>
          @if (!cameraOn()) {
            <div class="no-camera"><mat-icon>no_photography</mat-icon><span>{{ cameraMessage() }}</span></div>
          }
        }
        <svg class="oval" viewBox="0 0 100 133" preserveAspectRatio="none" aria-hidden="true">
          <defs><mask id="m"><rect width="100" height="133" fill="white"/><ellipse cx="50" cy="60" rx="31" ry="41" fill="black"/></mask></defs>
          <rect width="100" height="133" fill="rgb(0 0 0 / .45)" mask="url(#m)"/>
          <ellipse cx="50" cy="60" rx="31" ry="41" fill="none" stroke="white" stroke-width="1" stroke-dasharray="3 2"/>
        </svg>
      </div>

      <div class="actions">
        @if (preview()) {
          <button mat-stroked-button (click)="retake()"><mat-icon>replay</mat-icon> Retake</button>
          <button mat-flat-button class="cta" (click)="usePhoto()"><mat-icon>check</mat-icon> Use this selfie</button>
        } @else {
          @if (cameraOn()) {
            <button mat-fab extended class="shutter" (click)="capture()"><mat-icon>photo_camera</mat-icon> Capture</button>
          }
          <button mat-button (click)="fileInput.click()"><mat-icon>add_a_photo</mat-icon> {{ cameraOn() ? 'Use phone camera app instead' : 'Open camera' }}</button>
        }
      </div>
      <input #fileInput type="file" accept="image/*" capture="user" hidden (change)="onFile($event)" />
      <button mat-button class="back" (click)="back.emit()"><mat-icon>arrow_back</mat-icon> Back</button>
    </main>
  `,
  styles: `
    .step { min-height: 100dvh; max-width: 480px; margin: 0 auto; padding: 20px 16px; display: flex; flex-direction: column; align-items: center; gap: 10px; text-align: center; }
    h1 { font: var(--mat-sys-headline-small); margin: 0; }
    .muted { color: var(--mat-sys-on-surface-variant); margin: 0; }
    .error { display: flex; gap: 6px; align-items: center; background: var(--mat-sys-error-container); color: var(--mat-sys-on-error-container);
      padding: 10px 14px; border-radius: 12px; margin: 0; text-align: left; }
    .viewer { position: relative; width: min(100%, 360px); aspect-ratio: 3 / 4; border-radius: 24px; overflow: hidden; background: #222; }
    video, img { width: 100%; height: 100%; object-fit: cover; display: block; }
    video { transform: scaleX(-1); } /* mirror like a real mirror; the captured photo is NOT mirrored */
    .hidden { display: none; }
    .oval { position: absolute; inset: 0; width: 100%; height: 100%; pointer-events: none; }
    /* Message sits below the oval so the dashed guide never crosses the text. */
    .no-camera { position: absolute; inset: auto 0 0 0; display: flex; align-items: center; justify-content: center; gap: 8px;
      color: #ddd; padding: 12px 20px 18px; font: var(--mat-sys-body-small); z-index: 1; }
    .actions { display: flex; flex-direction: column; align-items: center; gap: 8px; margin-top: 6px; }
    .actions > :first-child + .cta { order: -1; }
    .cta, .shutter { min-width: 220px; height: 52px; border-radius: 26px; }
    .back { margin-top: auto; }
  `,
})
export class SelfieStep {
  /** Message from the server about the previous attempt, e.g. "Move closer". */
  readonly error = input<string | null>(null);
  readonly captured = output<Blob>();
  readonly back = output<void>();

  private readonly video = viewChild<ElementRef<HTMLVideoElement>>('video');
  protected readonly cameraOn = signal(false);
  protected readonly cameraMessage = signal('Starting camera…');
  protected readonly preview = signal<string | null>(null);
  private blob: Blob | null = null;
  private stream: MediaStream | null = null;

  constructor() {
    afterNextRender(() => void this.startCamera());
    inject(DestroyRef).onDestroy(() => {
      this.stopCamera();
      if (this.preview()) URL.revokeObjectURL(this.preview()!);
    });
  }

  private async startCamera(): Promise<void> {
    if (!navigator.mediaDevices?.getUserMedia) {
      this.cameraMessage.set('Live camera isn’t available here. Tap “Open camera” below.');
      return;
    }
    try {
      this.stream = await navigator.mediaDevices.getUserMedia({
        video: { facingMode: 'user', width: { ideal: 1280 }, height: { ideal: 1280 } },
        audio: false,
      });
      const el = this.video()?.nativeElement;
      if (!el) return this.stopCamera();
      el.srcObject = this.stream;
      await el.play().catch(() => undefined);
      this.cameraOn.set(true);
    } catch {
      this.cameraMessage.set('We couldn’t open the camera. Allow camera access, or tap “Open camera” below.');
    }
  }

  private stopCamera(): void {
    this.stream?.getTracks().forEach((t) => t.stop());
    this.stream = null;
    this.cameraOn.set(false);
  }

  protected capture(): void {
    const el = this.video()?.nativeElement;
    if (!el || !el.videoWidth) return;
    const canvas = document.createElement('canvas');
    canvas.width = el.videoWidth;
    canvas.height = el.videoHeight;
    canvas.getContext('2d')!.drawImage(el, 0, 0);
    canvas.toBlob((b) => b && this.show(b), 'image/jpeg', 0.92);
  }

  protected onFile(e: Event): void {
    const input = e.target as HTMLInputElement;
    const file = input.files?.[0];
    input.value = '';
    if (file) this.show(file);
  }

  private show(blob: Blob): void {
    this.blob = blob;
    this.stopCamera();
    this.preview.set(URL.createObjectURL(blob));
  }

  protected retake(): void {
    if (this.preview()) URL.revokeObjectURL(this.preview()!);
    this.preview.set(null);
    this.blob = null;
    // The <video> element is re-created by the template; start the camera once it's there.
    setTimeout(() => void this.startCamera());
  }

  protected usePhoto(): void {
    if (this.blob) this.captured.emit(this.blob);
  }
}
