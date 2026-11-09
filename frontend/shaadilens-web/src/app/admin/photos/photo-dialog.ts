import { DatePipe, DecimalPipe } from '@angular/common';
import { Component, inject, resource, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { MatButtonModule } from '@angular/material/button';
import { MAT_DIALOG_DATA, MatDialogModule, MatDialogRef } from '@angular/material/dialog';
import { MatIconModule } from '@angular/material/icon';
import { MatMenuModule } from '@angular/material/menu';
import { MatSlideToggleModule } from '@angular/material/slide-toggle';
import { MatTooltipModule } from '@angular/material/tooltip';
import { AdminApi } from '../../core/api/admin-api';
import { EventDetail, UpdatePhotoRequest } from '../../core/api/api.models';
import { Notify } from '../../core/notify';

export interface PhotoDialogData {
  photoId: string;
  event: EventDetail;
}

/** Photo detail: the web image with detected face boxes drawn on top, plus admin actions. */
@Component({
  selector: 'app-photo-dialog',
  imports: [DatePipe, DecimalPipe, FormsModule, MatButtonModule, MatDialogModule, MatIconModule, MatMenuModule, MatSlideToggleModule, MatTooltipModule],
  template: `
    @if (detail.value(); as d) {
      <h2 mat-dialog-title>{{ d.photo.fileName }}</h2>
      <mat-dialog-content class="body">
        <div class="stage">
          <div class="frame" [style.aspect-ratio]="d.photo.width + ' / ' + d.photo.height">
            @if (d.photo.webUrl) { <img [src]="d.photo.webUrl" [alt]="d.photo.fileName" /> }
            @if (showFaces()) {
              @for (f of d.faces; track f.id) {
                <div class="box" [style.left.%]="f.x * 100" [style.top.%]="f.y * 100" [style.width.%]="f.w * 100" [style.height.%]="f.h * 100"
                     [matTooltip]="'Detector ' + (f.detScore | number: '1.2-2') + ' · ' + (f.faceSizePx | number: '1.0-0') + ' px · turned ' + (f.yaw | number: '1.0-0') + '° · sharpness ' + (f.quality | number: '1.0-0') + (f.eyeAspectRatio !== null ? ' · eyes ' + (f.eyeAspectRatio | number: '1.2-2') : '')"></div>
              }
            }
          </div>
        </div>
        <aside class="info">
          <dl>
            <dt>Status</dt><dd>{{ d.photo.status }}@if (d.photo.error) { — <span class="error">{{ d.photo.error }}</span> }</dd>
            <dt>Faces</dt><dd>{{ d.faces.length }}</dd>
            <dt>Size</dt><dd>{{ d.photo.width }} × {{ d.photo.height }}</dd>
            <dt>Taken</dt><dd>{{ d.photo.takenAt ? (d.photo.takenAt | date: 'd MMM y, HH:mm' : 'UTC') : '—' }}</dd>
            <dt>Camera</dt><dd>{{ d.cameraModel ?? '—' }}</dd>
            <dt>Collection</dt><dd>{{ collectionName(d.photo.collectionId) }}</dd>
            <dt>Culling</dt>
            <dd>
              @if (!d.photo.analysed) { <span class="muted">not analysed yet</span> }
              @else {
                @if (d.photo.isBlurry) { <span class="flag"><mat-icon>blur_on</mat-icon> Blurry</span> }
                @if (d.photo.closedEyes) { <span class="flag"><mat-icon>visibility_off</mat-icon> Eyes closed ({{ d.photo.closedEyes }})</span> }
                @if (d.photo.burstGroupId) {
                  <span class="flag"><mat-icon>burst_mode</mat-icon> {{ d.photo.isBurstBest ? 'Best of its burst' : 'Near-duplicate' }}</span>
                }
                @if (!d.photo.isBlurry && !d.photo.closedEyes && !d.photo.burstGroupId) { Looks good }
              }
            </dd>
          </dl>
          <mat-slide-toggle [ngModel]="showFaces()" (ngModelChange)="showFaces.set($event)">Show face boxes</mat-slide-toggle>
          <div class="actions">
            @if (d.photo.burstGroupId && !d.photo.isBurstBest) {
              <button mat-flat-button (click)="keep()"><mat-icon>verified</mat-icon> Keep this one of the burst</button>
            }
            <button mat-stroked-button (click)="update({ isHighlight: !d.photo.isHighlight })">
              <mat-icon>{{ d.photo.isHighlight ? 'star' : 'star_outline' }}</mat-icon> {{ d.photo.isHighlight ? 'Unmark highlight' : 'Mark highlight' }}
            </button>
            <button mat-stroked-button (click)="update({ isHidden: !d.photo.isHidden })">
              <mat-icon>{{ d.photo.isHidden ? 'visibility' : 'visibility_off' }}</mat-icon> {{ d.photo.isHidden ? 'Show to guests' : 'Hide from guests' }}
            </button>
            <button mat-stroked-button [matMenuTriggerFor]="move"><mat-icon>drive_file_move</mat-icon> Move to…</button>
            <mat-menu #move="matMenu">
              @for (c of data.event.collections; track c.id) {
                <button mat-menu-item [disabled]="c.id === d.photo.collectionId" (click)="update({ collectionId: c.id })">{{ c.name }}</button>
              }
            </mat-menu>
            <button mat-stroked-button (click)="setCover()" [disabled]="!d.photo.webUrl"><mat-icon>wallpaper</mat-icon> Use as cover</button>
            <button mat-stroked-button (click)="reprocess()"><mat-icon>autorenew</mat-icon> Re-run face detection</button>
            <button mat-stroked-button class="danger" (click)="remove()"><mat-icon>delete</mat-icon> Delete</button>
          </div>
        </aside>
      </mat-dialog-content>
      <mat-dialog-actions align="end"><button mat-button [mat-dialog-close]="changed">Close</button></mat-dialog-actions>
    }
  `,
  styles: `
    .body { display: grid; grid-template-columns: minmax(0, 1fr) 280px; gap: 16px; }
    @media (max-width: 760px) { .body { grid-template-columns: 1fr; } }
    .stage { display: flex; justify-content: center; align-items: flex-start; }
    .frame { position: relative; max-width: 100%; max-height: 70vh; }
    .frame img { width: 100%; height: 100%; display: block; object-fit: contain; }
    .box { position: absolute; border: 2px solid #00e676; border-radius: 4px; box-shadow: 0 0 0 1px rgb(0 0 0 / .5); }
    dl { display: grid; grid-template-columns: auto 1fr; gap: 4px 12px; margin: 0 0 12px; }
    dt { color: var(--mat-sys-on-surface-variant); }
    dd { margin: 0; overflow-wrap: anywhere; }
    .actions { display: flex; flex-direction: column; gap: 8px; margin-top: 16px; }
    .actions button { justify-content: flex-start; }
    .error, .danger { color: var(--mat-sys-error); }
    .flag { display: inline-flex; align-items: center; gap: 2px; margin-right: 8px; }
    .flag mat-icon { font-size: 16px; width: 16px; height: 16px; }
  `,
})
export class PhotoDialog {
  protected readonly data = inject<PhotoDialogData>(MAT_DIALOG_DATA);
  private readonly api = inject(AdminApi);
  private readonly notify = inject(Notify);
  private readonly ref = inject(MatDialogRef<PhotoDialog, boolean>);

  protected readonly detail = resource({ loader: () => this.api.photo(this.data.photoId) });
  protected readonly showFaces = signal(true);
  protected changed = false;

  protected collectionName(id: string): string {
    return this.data.event.collections.find((c) => c.id === id)?.name ?? '—';
  }

  protected async update(req: UpdatePhotoRequest): Promise<void> {
    await this.act(() => this.api.updatePhoto(this.data.photoId, req), 'Saved');
  }

  protected async setCover(): Promise<void> {
    await this.act(() => this.api.setCover(this.data.event.id, this.data.photoId), 'Cover photo set');
  }

  protected async keep(): Promise<void> {
    await this.act(() => this.api.keepInBurst(this.data.photoId), 'This one is now the keeper');
  }

  protected async reprocess(): Promise<void> {
    await this.act(() => this.api.reprocess(this.data.photoId), 'Queued for face detection');
  }

  protected async remove(): Promise<void> {
    if (!confirm('Delete this photo for everyone? This cannot be undone.')) return;
    try {
      await this.api.deletePhoto(this.data.photoId);
      this.notify.ok('Photo deleted');
      this.ref.close(true);
    } catch (err) {
      this.notify.error(err);
    }
  }

  private async act(action: () => Promise<unknown>, ok: string): Promise<void> {
    try {
      await action();
      this.changed = true;
      this.notify.ok(ok);
      this.detail.reload();
    } catch (err) {
      this.notify.error(err);
    }
  }
}
