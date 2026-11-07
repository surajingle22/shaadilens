import { DecimalPipe, PercentPipe } from '@angular/common';
import { Component, computed, effect, inject, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { MatButtonModule } from '@angular/material/button';
import { MatFormFieldModule } from '@angular/material/form-field';
import { MatIconModule } from '@angular/material/icon';
import { MatProgressBarModule } from '@angular/material/progress-bar';
import { MatSelectModule } from '@angular/material/select';
import { Notify } from '../../core/notify';
import { EventContext } from '../event/event-context';
import { ACCEPTED_EXTENSIONS, MAX_FILE_MB, UploadItem, UploadQueue } from './upload-queue';

@Component({
  selector: 'app-uploader',
  imports: [DecimalPipe, PercentPipe, FormsModule, MatButtonModule, MatFormFieldModule, MatIconModule, MatProgressBarModule, MatSelectModule],
  template: `
    <section class="panel">
      <mat-form-field appearance="outline" class="collection">
        <mat-label>Upload into collection</mat-label>
        <mat-select [ngModel]="collectionId()" (ngModelChange)="collectionId.set($event)">
          @for (c of ctx.event.value()?.collections ?? []; track c.id) {
            <mat-option [value]="c.id">{{ c.name }} ({{ c.photoCount }})</mat-option>
          }
        </mat-select>
      </mat-form-field>

      <div class="drop" [class.over]="dragOver()"
           (dragover)="$event.preventDefault(); dragOver.set(true)" (dragleave)="dragOver.set(false)" (drop)="onDrop($event)">
        <mat-icon class="big">cloud_upload</mat-icon>
        <p><strong>Drop photos or whole folders here</strong></p>
        <p class="muted small">JPEG, PNG, WebP or HEIC · up to {{ maxMb }} MB each · duplicates are skipped automatically</p>
        <div class="buttons">
          <button mat-flat-button (click)="files.click()" [disabled]="!collectionId()"><mat-icon>add_photo_alternate</mat-icon> Choose photos</button>
          <button mat-stroked-button (click)="folder.click()" [disabled]="!collectionId()"><mat-icon>folder</mat-icon> Choose folder</button>
        </div>
        <input #files type="file" multiple [accept]="accept" hidden (change)="onPick($event)" />
        <input #folder type="file" webkitdirectory multiple hidden (change)="onPick($event)" />
      </div>
    </section>

    @if (queue.items().length) {
      <section class="panel">
        <div class="summary">
          <div>
            <strong>{{ queue.overall() | percent }}</strong>
            <span class="muted">
              {{ queue.counts().done }} uploaded · {{ queue.counts().duplicate }} duplicates ·
              {{ queue.counts().uploading + queue.counts().queued }} remaining
              @if (queue.counts().error) { · <span class="error">{{ queue.counts().error }} failed</span> }
            </span>
          </div>
          <div class="buttons">
            @if (queue.counts().error) {
              <button mat-stroked-button (click)="queue.retryFailed()"><mat-icon>replay</mat-icon> Retry failed</button>
            }
            <button mat-button (click)="queue.clearFinished()">Clear finished</button>
          </div>
        </div>
        <mat-progress-bar mode="determinate" [value]="queue.overall() * 100" />

        <ul class="files">
          @for (item of visible(); track item.id) {
            <li>
              <mat-icon [class]="item.state">{{ icon(item) }}</mat-icon>
              <span class="name" [title]="item.file.name">{{ item.file.name }}</span>
              <span class="size muted">{{ item.file.size / 1048576 | number: '1.1-1' }} MB</span>
              @switch (item.state) {
                @case ('uploading') { <mat-progress-bar mode="determinate" [value]="item.progress * 100" class="bar" /> }
                @case ('error') { <span class="error small">{{ item.error }}</span> }
                @case ('duplicate') { <span class="muted small">already uploaded</span> }
                @case ('queued') { <span class="muted small">waiting</span> }
              }
            </li>
          }
        </ul>
        @if (queue.items().length > visible().length) {
          <p class="muted small">Showing the {{ visible().length }} most relevant of {{ queue.items().length }} files.</p>
        }
        <p class="muted small">You can switch tabs — uploads keep running. Don’t close this browser tab until they finish.</p>
      </section>
    }
  `,
  styles: `
    :host { display: flex; flex-direction: column; gap: 16px; }
    .collection { width: min(360px, 100%); }
    .drop {
      border: 2px dashed var(--mat-sys-outline-variant); border-radius: var(--mat-sys-corner-large);
      padding: 32px 16px; text-align: center; transition: background .15s, border-color .15s;
    }
    .drop.over { background: var(--mat-sys-primary-container); border-color: var(--mat-sys-primary); }
    .big { font-size: 48px; width: 48px; height: 48px; color: var(--mat-sys-primary); }
    .buttons { display: flex; gap: 8px; justify-content: center; flex-wrap: wrap; }
    .summary { display: flex; justify-content: space-between; align-items: center; flex-wrap: wrap; gap: 8px; margin-bottom: 8px; }
    .summary strong { font: var(--mat-sys-headline-small); margin-right: 8px; }
    .files { list-style: none; padding: 0; margin: 12px 0 0; max-height: 50vh; overflow: auto; }
    .files li { display: grid; grid-template-columns: 24px minmax(0, 1fr) 70px minmax(80px, 200px); gap: 8px; align-items: center; padding: 4px 0; }
    .name { overflow: hidden; text-overflow: ellipsis; white-space: nowrap; }
    .bar { width: 100%; }
    .done { color: #2e7d32; }
    .duplicate { color: var(--mat-sys-on-surface-variant); }
    .error { color: var(--mat-sys-error); }
    .uploading { color: var(--mat-sys-primary); }
  `,
})
export class Uploader {
  protected readonly ctx = inject(EventContext);
  protected readonly queue = inject(UploadQueue);
  private readonly notify = inject(Notify);

  protected readonly accept = ACCEPTED_EXTENSIONS.join(',') + ',image/*';
  protected readonly maxMb = MAX_FILE_MB;
  protected readonly collectionId = signal<string | null>(null);
  protected readonly dragOver = signal(false);

  /** Long queues: show active + failed first, then the rest, max 200 rows. */
  protected readonly visible = computed(() => {
    const order = { uploading: 0, error: 1, queued: 2, done: 3, duplicate: 4 };
    return [...this.queue.items()].sort((a, b) => order[a.state] - order[b.state]).slice(0, 200);
  });

  constructor() {
    // Default to the first collection once the event loads.
    effect(() => {
      const first = this.ctx.event.value()?.collections[0]?.id;
      if (first && !this.collectionId()) this.collectionId.set(first);
    });
    // Refresh counts on the overview when files finish.
    effect(() => {
      if (this.queue.counts().done > 0 && !this.queue.busy()) this.ctx.reload();
    });
  }

  protected onPick(e: Event): void {
    const input = e.target as HTMLInputElement;
    this.enqueue([...(input.files ?? [])]);
    input.value = '';
  }

  protected async onDrop(e: DragEvent): Promise<void> {
    e.preventDefault();
    this.dragOver.set(false);
    if (!this.collectionId()) return;
    // Folders: walk the dropped entries recursively.
    const entries = [...(e.dataTransfer?.items ?? [])].map((i) => i.webkitGetAsEntry()).filter((x): x is FileSystemEntry => !!x);
    const files = entries.length ? (await Promise.all(entries.map(readEntry))).flat() : [...(e.dataTransfer?.files ?? [])];
    this.enqueue(files);
  }

  protected icon(item: UploadItem): string {
    return { queued: 'schedule', uploading: 'upload', done: 'check_circle', duplicate: 'content_copy', error: 'error' }[item.state];
  }

  private enqueue(files: File[]): void {
    const eventId = this.ctx.eventId();
    const collectionId = this.collectionId();
    if (!eventId || !collectionId || !files.length) return;
    const skipped = this.queue.add(files, eventId, collectionId);
    if (skipped.length) this.notify.error(null, `Skipped ${skipped.length} file(s): ${skipped.slice(0, 3).join(', ')}${skipped.length > 3 ? '…' : ''}`);
  }
}

/** Reads a dropped file or (recursively) a dropped folder. */
async function readEntry(entry: FileSystemEntry): Promise<File[]> {
  if (entry.isFile) {
    return [await new Promise<File>((res, rej) => (entry as FileSystemFileEntry).file(res, rej))];
  }
  const reader = (entry as FileSystemDirectoryEntry).createReader();
  const children: FileSystemEntry[] = [];
  // readEntries returns results in batches (max ~100) until it returns an empty array.
  for (;;) {
    const batch = await new Promise<FileSystemEntry[]>((res, rej) => reader.readEntries(res, rej));
    if (!batch.length) break;
    children.push(...batch);
  }
  return (await Promise.all(children.map(readEntry))).flat();
}
