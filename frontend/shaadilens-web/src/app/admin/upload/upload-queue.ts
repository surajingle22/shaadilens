import { HttpClient, HttpEventType, HttpHeaders } from '@angular/common/http';
import { Injectable, computed, inject, signal } from '@angular/core';
import { Observable, filter, lastValueFrom, tap } from 'rxjs';
import { AdminApi } from '../../core/api/admin-api';
import { API } from '../../core/api/api-urls';
import { errorMessage } from '../../core/notify';

export type UploadState = 'queued' | 'uploading' | 'done' | 'duplicate' | 'error';

export interface UploadItem {
  id: number;
  file: File;
  eventId: string;
  collectionId: string;
  state: UploadState;
  /** 0..1 */
  progress: number;
  error?: string;
  /** Kept after a failure so a retry resumes instead of starting over. */
  uploadId?: string;
  chunkSize?: number;
}

export const ACCEPTED_EXTENSIONS = ['.jpg', '.jpeg', '.png', '.webp', '.heic', '.heif'];
export const MAX_FILE_MB = 50;
const PARALLEL_FILES = 4;
const CHUNK_RETRIES = 3;

/**
 * Upload queue (app-wide singleton, so uploads keep going while you browse other admin pages).
 *
 * Per file:  init → PUT chunk 0..N-1 (skipping chunks the server already has) → complete
 * 4 files upload in parallel; each chunk is retried 3× with a growing pause before the file is marked failed.
 * "Retry failed" resumes from the last chunk the server received.
 */
@Injectable({ providedIn: 'root' })
export class UploadQueue {
  private readonly api = inject(AdminApi);
  private readonly http = inject(HttpClient);
  private nextId = 1;

  readonly items = signal<UploadItem[]>([]);
  readonly active = computed(() => this.items().filter((i) => i.state === 'uploading').length);
  readonly counts = computed(() => {
    const c = { queued: 0, uploading: 0, done: 0, duplicate: 0, error: 0 };
    for (const i of this.items()) c[i.state]++;
    return c;
  });
  /** Overall progress by bytes, 0..1. */
  readonly overall = computed(() => {
    const items = this.items();
    const total = items.reduce((s, i) => s + i.file.size, 0);
    return total ? items.reduce((s, i) => s + i.file.size * i.progress, 0) / total : 0;
  });
  readonly busy = computed(() => this.counts().queued + this.counts().uploading > 0);

  /** Adds files; returns the names that were skipped (wrong type / too big). */
  add(files: File[], eventId: string, collectionId: string): string[] {
    const skipped: string[] = [];
    const accepted: UploadItem[] = [];
    for (const file of files) {
      const ext = file.name.slice(file.name.lastIndexOf('.')).toLowerCase();
      if (!ACCEPTED_EXTENSIONS.includes(ext) || file.size === 0) {
        if (!file.name.startsWith('.')) skipped.push(`${file.name} (not a photo)`);
        continue;
      }
      if (file.size > MAX_FILE_MB * 1024 * 1024) {
        skipped.push(`${file.name} (over ${MAX_FILE_MB} MB)`);
        continue;
      }
      accepted.push({ id: this.nextId++, file, eventId, collectionId, state: 'queued', progress: 0 });
    }
    this.items.update((list) => [...list, ...accepted]);
    this.pump();
    return skipped;
  }

  retryFailed(): void {
    this.items.update((list) => list.map((i) => (i.state === 'error' ? { ...i, state: 'queued', error: undefined } : i)));
    this.pump();
  }

  clearFinished(): void {
    this.items.update((list) => list.filter((i) => i.state !== 'done' && i.state !== 'duplicate'));
  }

  /** Start queued files until PARALLEL_FILES are running. */
  private pump(): void {
    while (this.active() < PARALLEL_FILES) {
      const next = this.items().find((i) => i.state === 'queued');
      if (!next) return;
      this.patch(next.id, { state: 'uploading' });
      void this.uploadFile(next.id);
    }
  }

  private async uploadFile(id: number): Promise<void> {
    try {
      let item = this.get(id);
      let received = new Set<number>();

      if (item.uploadId && item.chunkSize) {
        // Resume: ask the server which chunks already arrived and send only the rest.
        const status = await this.api.uploadStatus(item.uploadId);
        received = new Set(status.receivedChunks);
      } else {
        const init = await this.api.initUpload(item.eventId, item.collectionId, item.file);
        this.patch(id, { uploadId: init.uploadId, chunkSize: init.chunkSize });
      }

      item = this.get(id);
      const uploadId = item.uploadId!;
      const chunkSize = item.chunkSize!;
      const totalChunks = Math.max(1, Math.ceil(item.file.size / chunkSize));
      for (let index = 0; index < totalChunks; index++) {
        if (received.has(index)) continue;
        const start = index * chunkSize;
        const blob = item.file.slice(start, Math.min(start + chunkSize, item.file.size));
        await this.putChunkWithRetry(uploadId, index, blob, (loaded) =>
          this.patch(id, { progress: Math.min(0.99, (start + loaded) / item.file.size) }),
        );
      }

      const result = await this.api.completeUpload(uploadId);
      this.patch(id, { state: result.status === 'duplicate' ? 'duplicate' : 'done', progress: 1 });
    } catch (err) {
      this.patch(id, { state: 'error', error: errorMessage(err, 'Upload failed') });
    } finally {
      this.pump();
    }
  }

  private async putChunkWithRetry(uploadId: string, index: number, blob: Blob, onProgress: (loaded: number) => void) {
    for (let attempt = 1; ; attempt++) {
      try {
        await lastValueFrom(this.putChunk(uploadId, index, blob, onProgress));
        return;
      } catch (err) {
        if (attempt >= CHUNK_RETRIES) throw err;
        await new Promise((r) => setTimeout(r, 1000 * attempt * attempt)); // 1 s, 4 s
      }
    }
  }

  private putChunk(uploadId: string, index: number, blob: Blob, onProgress: (loaded: number) => void): Observable<unknown> {
    return this.http
      .put(API.uploadChunk(uploadId, index), blob, {
        headers: new HttpHeaders({ 'Content-Type': 'application/octet-stream' }),
        reportProgress: true,
        observe: 'events',
      })
      .pipe(
        tap((e) => e.type === HttpEventType.UploadProgress && onProgress(e.loaded)),
        filter((e) => e.type === HttpEventType.Response),
      );
  }

  private get(id: number): UploadItem {
    return this.items().find((i) => i.id === id)!;
  }

  private patch(id: number, changes: Partial<UploadItem>): void {
    this.items.update((list) => list.map((i) => (i.id === id ? { ...i, ...changes } : i)));
  }
}
