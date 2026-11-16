import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { TestBed } from '@angular/core/testing';
import { UploadQueue, readChunk } from './upload-queue';

const file = (name: string, bytes: number) => new File([new Uint8Array(bytes)], name, { type: 'image/jpeg' });

describe('UploadQueue', () => {
  let queue: UploadQueue;
  let http: HttpTestingController;

  beforeEach(() => {
    TestBed.configureTestingModule({ providers: [provideHttpClient(), provideHttpClientTesting()] });
    queue = TestBed.inject(UploadQueue);
    http = TestBed.inject(HttpTestingController);
  });

  it('skips non-photos and files over 50 MB', () => {
    const skipped = queue.add([file('a.jpg', 10), file('notes.txt', 10), file('huge.jpg', 51 * 1024 * 1024)], 'ev', 'col');
    expect(skipped).toEqual(['notes.txt (not a photo)', 'huge.jpg (over 50 MB)']);
    expect(queue.items().map((i) => i.file.name)).toEqual(['a.jpg']);
    http.expectOne('/api/v1/events/ev/uploads'); // the accepted one started
  });

  it('uploads in chunks, then completes', async () => {
    queue.add([file('big.jpg', 25)], 'ev', 'col');

    const init = http.expectOne('/api/v1/events/ev/uploads');
    expect(init.request.body).toEqual({ collectionId: 'col', fileName: 'big.jpg', size: 25 });
    init.flush({ uploadId: 'u1', chunkSize: 10, totalChunks: 3 });

    for (const [index, size] of [[0, 10], [1, 10], [2, 5]]) {
      const put = await vi.waitFor(() => http.expectOne(`/api/v1/uploads/u1/chunks/${index}`));
      // Plain bytes, not a lazy Blob slice (iPhone Safari sent empty bodies for those).
      expect(put.request.body).toBeInstanceOf(ArrayBuffer);
      expect((put.request.body as ArrayBuffer).byteLength).toBe(size);
      put.flush(null);
    }

    (await vi.waitFor(() => http.expectOne('/api/v1/uploads/u1/complete'))).flush({ status: 'created', photoId: 'p1' });
    await vi.waitFor(() => expect(queue.items()[0].state).toBe('done'));
    expect(queue.overall()).toBe(1);
  });

  it('marks duplicates', async () => {
    queue.add([file('dup.jpg', 5)], 'ev', 'col');
    http.expectOne('/api/v1/events/ev/uploads').flush({ uploadId: 'u2', chunkSize: 10, totalChunks: 1 });
    (await vi.waitFor(() => http.expectOne('/api/v1/uploads/u2/chunks/0'))).flush(null);
    (await vi.waitFor(() => http.expectOne('/api/v1/uploads/u2/complete'))).flush({ status: 'duplicate', photoId: 'p0' });
    await vi.waitFor(() => expect(queue.items()[0].state).toBe('duplicate'));
  });

  it('retry resumes: only missing chunks are sent again', async () => {
    vi.useFakeTimers({ shouldAdvanceTime: true });
    queue.add([file('r.jpg', 20)], 'ev', 'col');
    http.expectOne('/api/v1/events/ev/uploads').flush({ uploadId: 'u3', chunkSize: 10, totalChunks: 2 });
    (await vi.waitFor(() => http.expectOne('/api/v1/uploads/u3/chunks/0'))).flush(null);

    // Chunk 1 fails 3 times (network down) → file marked as failed.
    for (let attempt = 0; attempt < 3; attempt++) {
      (await vi.waitFor(() => http.expectOne('/api/v1/uploads/u3/chunks/1'))).error(new ProgressEvent('error'));
      await vi.advanceTimersByTimeAsync(10_000);
    }
    await vi.waitFor(() => expect(queue.items()[0].state).toBe('error'));

    queue.retryFailed();
    (await vi.waitFor(() => http.expectOne('/api/v1/uploads/u3'))).flush({ uploadId: 'u3', totalChunks: 2, receivedChunks: [0], photoId: null });
    (await vi.waitFor(() => http.expectOne('/api/v1/uploads/u3/chunks/1'))).flush(null);
    (await vi.waitFor(() => http.expectOne('/api/v1/uploads/u3/complete'))).flush({ status: 'created', photoId: 'p3' });
    await vi.waitFor(() => expect(queue.items()[0].state).toBe('done'));
    http.verify(); // chunk 0 was NOT re-sent
    vi.useRealTimers();
  });

  it('a photo the device cannot read fails with a clear message and sends nothing', async () => {
    // Like an iPhone photo whose original isn't readable: the size is known, but reading returns nothing.
    const unreadable = file('icloud.jpg', 12);
    vi.spyOn(unreadable, 'slice').mockReturnValue(new Blob([]));
    queue.add([unreadable], 'ev', 'col');
    http.expectOne('/api/v1/events/ev/uploads').flush({ uploadId: 'u4', chunkSize: 10, totalChunks: 2 });

    await vi.waitFor(() => expect(queue.items()[0].state).toBe('error'));
    expect(queue.items()[0].error).toContain("Couldn't read this photo");
    http.expectNone('/api/v1/uploads/u4/chunks/0');
  });
});

describe('readChunk', () => {
  it('returns exactly the requested bytes', async () => {
    const bytes = new Uint8Array([1, 2, 3, 4, 5, 6]);
    const chunk = await readChunk(new Blob([bytes]), 2, 5);
    expect(Array.from(new Uint8Array(chunk))).toEqual([3, 4, 5]);
  });
});
