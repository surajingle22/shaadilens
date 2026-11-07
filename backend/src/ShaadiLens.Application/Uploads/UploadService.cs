using System.Security.Cryptography;
using FluentValidation;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using ShaadiLens.Application.Abstractions;
using ShaadiLens.Application.Common;
using ShaadiLens.Domain.Entities;

namespace ShaadiLens.Application.Uploads;

public sealed class UploadOptions
{
    public const string Section = "Uploads";
    public int MaxFileSizeMb { get; set; } = 50;
    /// <summary>8 MB chunks: small enough to retry cheaply on venue Wi-Fi, big enough to be efficient.</summary>
    public int ChunkSizeBytes { get; set; } = 8 * 1024 * 1024;

    public static readonly string[] AllowedExtensions = [".jpg", ".jpeg", ".png", ".webp", ".heic", ".heif"];
}

public sealed record InitUploadRequest(Guid CollectionId, string FileName, long Size);
public sealed record InitUploadResponse(Guid UploadId, int ChunkSize, int TotalChunks);
public sealed record UploadStatusResponse(Guid UploadId, int TotalChunks, IReadOnlyList<int> ReceivedChunks, Guid? PhotoId);

/// <param name="Status">"created" or "duplicate".</param>
public sealed record CompleteUploadResponse(string Status, Guid PhotoId);

public sealed class InitUploadRequestValidator : AbstractValidator<InitUploadRequest>
{
    public InitUploadRequestValidator(IOptions<UploadOptions> options)
    {
        var maxBytes = (long)options.Value.MaxFileSizeMb * 1024 * 1024;
        RuleFor(x => x.CollectionId).NotEmpty();
        RuleFor(x => x.FileName).NotEmpty().MaximumLength(255)
            .Must(n => UploadOptions.AllowedExtensions.Contains(Path.GetExtension(n).ToLowerInvariant()))
            .WithMessage("Only JPEG, PNG, WebP and HEIC photos are supported");
        RuleFor(x => x.Size).GreaterThan(0).LessThanOrEqualTo(maxBytes)
            .WithMessage($"Files must be at most {options.Value.MaxFileSizeMb} MB");
    }
}

/// <summary>
/// Resumable chunked upload:
///   1. POST init            → uploadId, chunkSize, totalChunks
///   2. PUT  chunk 0..N-1    → each written to tmp/uploads/{id}/{index}.part (re-sending a chunk is fine)
///   3. GET  status          → which chunks the server already has (resume after a dropped connection)
///   4. POST complete        → chunks are joined + SHA-256 hashed; duplicates are skipped;
///                              a Photo row is created and background processing is queued.
/// </summary>
public sealed class UploadService(
    IAppDbContext db,
    IStorageService storage,
    IPhotoPipelineScheduler pipeline,
    IOptions<UploadOptions> options,
    ILogger<UploadService> log)
{
    private readonly UploadOptions _opts = options.Value;

    public async Task<InitUploadResponse> InitAsync(Guid eventId, InitUploadRequest req, CancellationToken ct)
    {
        var collectionOk = await db.Collections.AnyAsync(c => c.Id == req.CollectionId && c.EventId == eventId, ct);
        if (!collectionOk) throw new NotFoundException("Collection");

        var total = (int)Math.Ceiling(req.Size / (double)_opts.ChunkSizeBytes);
        var session = new UploadSession
        {
            EventId = eventId,
            CollectionId = req.CollectionId,
            FileName = Path.GetFileName(req.FileName),
            Size = req.Size,
            ChunkSize = _opts.ChunkSizeBytes,
            TotalChunks = total,
        };
        db.UploadSessions.Add(session);
        await db.SaveChangesAsync(ct);
        return new InitUploadResponse(session.Id, session.ChunkSize, total);
    }

    public async Task PutChunkAsync(Guid uploadId, int index, Stream body, CancellationToken ct)
    {
        var s = await FindAsync(uploadId, ct);
        if (s.PhotoId is not null) throw new ConflictException("Upload already completed");
        if (index < 0 || index >= s.TotalChunks) throw new BadRequestException($"Chunk index must be 0..{s.TotalChunks - 1}");

        // Copy with a hard size cap so a client can't stream gigabytes into one chunk.
        var key = StorageKeys.UploadChunk(uploadId, index);
        await using (var target = storage.OpenWrite(key))
        {
            var copied = await CopyWithLimitAsync(body, target, s.ChunkSize, ct);
            var expected = index < s.TotalChunks - 1 ? s.ChunkSize : s.Size - (long)s.ChunkSize * (s.TotalChunks - 1);
            if (copied != expected)
            {
                await target.DisposeAsync();
                await storage.DeleteAsync(key, ct);
                throw new BadRequestException($"Chunk {index} should be {expected} bytes but was {copied}");
            }
        }
    }

    public async Task<UploadStatusResponse> GetStatusAsync(Guid uploadId, CancellationToken ct)
    {
        var s = await FindAsync(uploadId, ct);
        var received = s.PhotoId is null ? await ReceivedChunksAsync(uploadId, ct) : Enumerable.Range(0, s.TotalChunks).ToList();
        return new UploadStatusResponse(uploadId, s.TotalChunks, received, s.PhotoId);
    }

    public async Task<CompleteUploadResponse> CompleteAsync(Guid uploadId, CancellationToken ct)
    {
        var s = await FindAsync(uploadId, ct);
        if (s.PhotoId is { } done) return new CompleteUploadResponse("created", done); // idempotent retry

        var received = await ReceivedChunksAsync(uploadId, ct);
        if (received.Count != s.TotalChunks)
        {
            var missing = Enumerable.Range(0, s.TotalChunks).Except(received);
            throw new BadRequestException($"Missing chunks: {string.Join(",", missing.Take(20))}");
        }

        // Join chunks into the final file while computing SHA-256 in the same pass.
        var photo = new Photo { EventId = s.EventId, CollectionId = s.CollectionId, OriginalFileName = s.FileName };
        var ext = Path.GetExtension(s.FileName).ToLowerInvariant();
        photo.StorageKeyOriginal = StorageKeys.Original(s.EventId, photo.Id, ext == ".jpeg" ? ".jpg" : ext);

        using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        await using (var output = storage.OpenWrite(photo.StorageKeyOriginal))
        {
            var buffer = new byte[81920];
            for (var i = 0; i < s.TotalChunks; i++)
            {
                await using var chunk = await storage.OpenReadAsync(StorageKeys.UploadChunk(uploadId, i), ct);
                int read;
                while ((read = await chunk.ReadAsync(buffer, ct)) > 0)
                {
                    sha.AppendData(buffer, 0, read);
                    await output.WriteAsync(buffer.AsMemory(0, read), ct);
                }
            }
        }
        photo.ContentHash = Convert.ToHexStringLower(sha.GetHashAndReset());

        // Same bytes already uploaded to this event? Skip it.
        var existing = await db.Photos.Where(p => p.EventId == s.EventId && p.ContentHash == photo.ContentHash)
            .Select(p => (Guid?)p.Id).FirstOrDefaultAsync(ct);
        if (existing is { } dupId)
        {
            await storage.DeletePrefixAsync(StorageKeys.PhotoFolder(s.EventId, photo.Id), ct);
            await CleanupAsync(s, dupId, ct);
            log.LogInformation("Upload {UploadId} is a duplicate of photo {PhotoId}", uploadId, dupId);
            return new CompleteUploadResponse("duplicate", dupId);
        }

        db.Photos.Add(photo);
        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException)
        {
            // Two identical files completed at the same moment: the unique index (EventId, ContentHash) caught it.
            db.Photos.Entry(photo).State = EntityState.Detached;
            await storage.DeletePrefixAsync(StorageKeys.PhotoFolder(s.EventId, photo.Id), ct);
            var winner = await db.Photos.Where(p => p.EventId == s.EventId && p.ContentHash == photo.ContentHash)
                .Select(p => p.Id).FirstAsync(ct);
            await CleanupAsync(s, winner, ct);
            return new CompleteUploadResponse("duplicate", winner);
        }

        await CleanupAsync(s, photo.Id, ct);
        pipeline.EnqueueProcessing(photo.Id);
        return new CompleteUploadResponse("created", photo.Id);
    }

    private async Task CleanupAsync(UploadSession s, Guid photoId, CancellationToken ct)
    {
        s.PhotoId = photoId;
        await db.SaveChangesAsync(ct);
        await storage.DeletePrefixAsync(StorageKeys.UploadFolder(s.Id), ct);
    }

    private async Task<UploadSession> FindAsync(Guid id, CancellationToken ct) =>
        await db.UploadSessions.FirstOrDefaultAsync(u => u.Id == id, ct) ?? throw new NotFoundException("Upload");

    private async Task<List<int>> ReceivedChunksAsync(Guid uploadId, CancellationToken ct)
    {
        var names = await storage.ListAsync(StorageKeys.UploadFolder(uploadId), ct);
        return names.Where(n => n.EndsWith(".part"))
            .Select(n => int.Parse(Path.GetFileNameWithoutExtension(n)))
            .Order().ToList();
    }

    private static async Task<long> CopyWithLimitAsync(Stream source, Stream target, long limit, CancellationToken ct)
    {
        var buffer = new byte[81920];
        long total = 0;
        int read;
        while ((read = await source.ReadAsync(buffer, ct)) > 0)
        {
            total += read;
            if (total > limit) throw new BadRequestException($"Chunk larger than {limit} bytes");
            await target.WriteAsync(buffer.AsMemory(0, read), ct);
        }
        return total;
    }
}
