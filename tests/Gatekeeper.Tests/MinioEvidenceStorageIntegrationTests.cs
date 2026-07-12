using System.Text;
using Gatekeeper.Infrastructure;
using Minio;
using Testcontainers.Minio;
using Xunit;

namespace Gatekeeper.Tests;

/// <summary>
/// Real, ephemeral MinIO container — for behavior a fake can't honestly reproduce: actual bucket
/// creation, actual object storage/retrieval against the real Minio .NET SDK. One container for
/// the whole run (see PostgresFixture for the same reasoning on the Postgres side).
/// </summary>
public sealed class MinioFixture : IAsyncLifetime
{
    private readonly MinioContainer _container = new MinioBuilder()
        .WithUsername("test-access-key")
        .WithPassword("test-secret-key")
        .Build();

    public Task InitializeAsync() => _container.StartAsync();

    public Task DisposeAsync() => _container.DisposeAsync().AsTask();

    public IMinioClient CreateClient() => new MinioClient()
        .WithEndpoint(_container.GetConnectionString().Replace("http://", "").Replace("https://", ""))
        .WithCredentials("test-access-key", "test-secret-key")
        .WithSSL(false)
        .Build();
}

[CollectionDefinition("Minio")]
public sealed class MinioCollection : ICollectionFixture<MinioFixture>;

[Collection("Minio")]
public sealed class MinioEvidenceStorageIntegrationTests(MinioFixture fixture)
{
    private const string BucketName = "evidence-test";

    [Fact]
    public async Task SaveAsync_Then_OpenReadAsync_RoundTrips_The_Same_Bytes()
    {
        var storage = new MinioEvidenceStorage(fixture.CreateClient(), BucketName);
        var original = Encoding.UTF8.GetBytes("this is a fake screenshot, for testing only");

        using var input = new MemoryStream(original);
        var (storagePath, hash, sizeBytes) = await storage.SaveAsync(input, "image/png");

        Assert.StartsWith("evidence/", storagePath);
        Assert.NotEmpty(hash);
        Assert.Equal(original.LongLength, sizeBytes);

        await using var readBack = await storage.OpenReadAsync(storagePath);
        Assert.NotNull(readBack);
        using var buffer = new MemoryStream();
        await readBack.CopyToAsync(buffer);
        Assert.Equal(original, buffer.ToArray());
    }

    [Fact]
    public async Task SaveAsync_Is_Content_Addressed_Reuploading_Same_Bytes_Gives_Same_Path()
    {
        var storage = new MinioEvidenceStorage(fixture.CreateClient(), BucketName);
        var bytes = Encoding.UTF8.GetBytes("identical content, uploaded twice");

        using var first = new MemoryStream(bytes);
        var (path1, hash1, _) = await storage.SaveAsync(first, "image/png");

        using var second = new MemoryStream(bytes);
        var (path2, hash2, _) = await storage.SaveAsync(second, "image/png");

        Assert.Equal(path1, path2);
        Assert.Equal(hash1, hash2);
    }

    [Fact]
    public async Task OpenReadAsync_Returns_Null_For_Unknown_Path()
    {
        var storage = new MinioEvidenceStorage(fixture.CreateClient(), BucketName);
        var result = await storage.OpenReadAsync("evidence/does-not-exist");
        Assert.Null(result);
    }
}
