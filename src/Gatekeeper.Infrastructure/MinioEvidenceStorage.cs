namespace Gatekeeper.Infrastructure;

using System.Security.Cryptography;
using Gatekeeper.Application;
using Minio;
using Minio.DataModel.Args;

/// <summary>
/// Screenshot-evidence storage for moderation actions, backed by a MinIO bucket. Content-addressed
/// object keys (evidence/{sha256}) give free integrity checking and dedup — re-uploading the same
/// file is a cheap no-op, not a second copy. Buffers into memory to hash before upload (screenshots
/// are small; same "fully buffer" tradeoff the existing avatar proxy already makes for Telegram
/// photos — see WebEndpoints.cs).
/// </summary>
public sealed class MinioEvidenceStorage(IMinioClient client, string bucketName) : IEvidenceStorage
{
    public async Task<(string StoragePath, string ContentHash, long SizeBytes)> SaveAsync(
        Stream content, string contentType, CancellationToken ct = default)
    {
        using var buffer = new MemoryStream();
        await content.CopyToAsync(buffer, ct);
        var bytes = buffer.ToArray();
        var hash = Convert.ToHexStringLower(SHA256.HashData(bytes));
        var objectKey = $"evidence/{hash}";

        await EnsureBucketExistsAsync(ct);

        buffer.Position = 0;
        await client.PutObjectAsync(new PutObjectArgs()
            .WithBucket(bucketName)
            .WithObject(objectKey)
            .WithStreamData(buffer)
            .WithObjectSize(bytes.LongLength)
            .WithContentType(contentType), ct);

        return (objectKey, hash, bytes.LongLength);
    }

    public async Task<Stream?> OpenReadAsync(string storagePath, CancellationToken ct = default)
    {
        var result = new MemoryStream();
        try
        {
            await client.GetObjectAsync(new GetObjectArgs()
                .WithBucket(bucketName)
                .WithObject(storagePath)
                .WithCallbackStream(async (stream, innerCt) => await stream.CopyToAsync(result, innerCt)), ct);
        }
        // BucketNotFoundException happens if no evidence was ever saved yet (SaveAsync is the only
        // thing that creates the bucket) — same "we don't have this" outcome as a missing object.
        catch (Exception ex) when (ex is Minio.Exceptions.ObjectNotFoundException or Minio.Exceptions.BucketNotFoundException)
        {
            return null;
        }

        result.Position = 0;
        return result;
    }

    private async Task EnsureBucketExistsAsync(CancellationToken ct)
    {
        var exists = await client.BucketExistsAsync(new BucketExistsArgs().WithBucket(bucketName), ct);
        if (!exists)
            await client.MakeBucketAsync(new MakeBucketArgs().WithBucket(bucketName), ct);
    }
}
