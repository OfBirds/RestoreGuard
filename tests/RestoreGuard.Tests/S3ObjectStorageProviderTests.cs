using System.Net;
using RestoreGuard.Providers.S3;

namespace RestoreGuard.Tests;

public class S3ObjectStorageProviderTests
{
    private static string Fixture(string name) =>
        Path.Combine(AppContext.BaseDirectory, "Fixtures", name);

    private static S3BucketConfig Config(
        bool checkObjectLock = true,
        bool checkNewestObject = true,
        int maxListKeys = 2,
        int maxListRequests = 5) => new(
        "offsite",
        "https://s3.example.com",
        "homelab-backups",
        Prefix: "backup/",
        AccessKey: "AKIDEXAMPLE",
        SecretKey: "secret",
        CheckObjectLock: checkObjectLock,
        CheckNewestObject: checkNewestObject,
        MaxListKeys: maxListKeys,
        MaxListRequests: maxListRequests);

    [Fact]
    public void ParseFixture_FindsEnabledVersioning()
    {
        var versioning = S3ObjectStorageProvider.ParseVersioning(
            File.ReadAllText(Fixture("s3-versioning-enabled.xml")));
        Assert.True(versioning.VersioningEnabled);
        Assert.False(versioning.MfaDeleteEnabled);
    }

    [Fact]
    public void ParseFixture_FindsDefaultComplianceRetention()
    {
        var lockState = S3ObjectStorageProvider.ParseObjectLock(
            File.ReadAllText(Fixture("s3-object-lock-enabled.xml")));
        Assert.True(lockState.Enabled);
        Assert.True(lockState.HasDefaultRetention);
        Assert.Equal("COMPLIANCE", lockState.Mode);
        Assert.Equal(30, lockState.RetentionDays);
    }

    [Fact]
    public void ParseObjectLock_WithoutRule_IsEnabledWithoutDefaultRetention()
    {
        var lockState = S3ObjectStorageProvider.ParseObjectLock(
            File.ReadAllText(Fixture("s3-object-lock-no-default-retention.xml")));

        Assert.True(lockState.Enabled);
        Assert.False(lockState.HasDefaultRetention);
    }

    [Fact]
    public void ParseFixture_FindsObjectSummariesAndContinuation()
    {
        var page = S3ObjectStorageProvider.ParseListPage(
            File.ReadAllText(Fixture("s3-list-objects-page-one.xml")));
        Assert.Equal(2, page.Objects.Count);
        Assert.True(page.IsTruncated);
        Assert.Equal("page-two", page.NextContinuationToken);
        Assert.Equal(64, page.Objects[0].SizeBytes);
    }

    [Fact]
    public async Task BoundedPagination_FollowsContinuationAndSelectsNewest()
    {
        var requestUris = new List<string>();
        var handler = new FakeS3Handler((uri, query) =>
        {
            requestUris.Add(uri);
            if (query.Contains("versioning", StringComparison.Ordinal))
                return File.ReadAllText(Fixture("s3-versioning-enabled.xml"));
            if (query.Contains("object-lock", StringComparison.Ordinal))
                return File.ReadAllText(Fixture("s3-object-lock-enabled.xml"));
            if (query.Contains("continuation-token=page-two", StringComparison.Ordinal))
                return File.ReadAllText(Fixture("s3-list-objects-page-two.xml"));
            return File.ReadAllText(Fixture("s3-list-objects-page-one.xml"));
        });
        var provider = new S3ObjectStorageProvider(handler);
        var audit = await provider.GetBucketAsync(Config(), "unused");

        Assert.Equal(2, requestUris.Count(uri => uri.Contains("max-keys=2", StringComparison.Ordinal)));
        Assert.All(requestUris.Where(uri => uri.Contains("max-keys=2", StringComparison.Ordinal)),
            uri => Assert.Contains("max-keys=2", uri));
        Assert.Equal("backup/newest.json", audit.NewestObject!.Key);
        Assert.False(audit.NewestObjectListingIncomplete);
        Assert.All(handler.AuthorizationHeaders,
            header => Assert.StartsWith("AWS4-HMAC-SHA256", header, StringComparison.Ordinal));
    }

    [Fact]
    public async Task BoundedPagination_StopsAtRequestCapAndMarksListingIncomplete()
    {
        var requestCount = 0;
        var handler = new FakeS3Handler((_, query) =>
        {
            requestCount++;
            return query.Contains("versioning", StringComparison.Ordinal)
                ? File.ReadAllText(Fixture("s3-versioning-enabled.xml"))
                : query.Contains("object-lock", StringComparison.Ordinal)
                    ? File.ReadAllText(Fixture("s3-object-lock-enabled.xml"))
                    : File.ReadAllText(Fixture("s3-list-objects-page-one.xml"));
        });
        var provider = new S3ObjectStorageProvider(handler);
        var audit = await provider.GetBucketAsync(Config(maxListRequests: 2), "unused");

        Assert.Equal(2, requestCount - 2);
        Assert.True(audit.NewestObjectListingIncomplete);
    }

    [Fact]
    public async Task BoundedPagination_RetainsNewestObjectAcrossPages()
    {
        var handler = new FakeS3Handler((_, query) =>
        {
            if (query.Contains("versioning", StringComparison.Ordinal))
                return File.ReadAllText(Fixture("s3-versioning-enabled.xml"));
            if (query.Contains("object-lock", StringComparison.Ordinal))
                return File.ReadAllText(Fixture("s3-object-lock-enabled.xml"));
            return query.Contains("continuation-token=page-two", StringComparison.Ordinal)
                ? File.ReadAllText(Fixture("s3-list-objects-page-two-older.xml"))
                : File.ReadAllText(Fixture("s3-list-objects-page-one.xml"));
        });

        var audit = await new S3ObjectStorageProvider(handler).GetBucketAsync(Config(), "unused");

        Assert.Equal("backup/older-than-page-two.json", audit.NewestObject!.Key);
        Assert.False(audit.NewestObjectListingIncomplete);
    }

    [Fact]
    public async Task MissingObjectLockConfiguration_IsReportedAsDisabled()
    {
        var handler = new FakeS3Handler(
            (_, query) => query.Contains("versioning", StringComparison.Ordinal)
                ? File.ReadAllText(Fixture("s3-versioning-enabled.xml"))
                : File.ReadAllText(Fixture("s3-list-objects-page-one.xml")),
            (_, query) => query.Contains("object-lock", StringComparison.Ordinal)
                ? new HttpResponseMessage(HttpStatusCode.NotFound)
                {
                    Content = new StringContent(File.ReadAllText(Fixture("s3-object-lock-not-configured.xml"))),
                }
                : null);

        var audit = await new S3ObjectStorageProvider(handler).GetBucketAsync(
            Config(checkNewestObject: false), "unused");

        Assert.NotNull(audit.ObjectLock);
        Assert.False(audit.ObjectLock!.Enabled);
    }

    private sealed class FakeS3Handler(
        Func<string, string, string> responder,
        Func<string, string, HttpResponseMessage?>? overrideResponse = null) : HttpMessageHandler
    {
        public List<string> AuthorizationHeaders { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            AuthorizationHeaders.Add(
                request.Headers.TryGetValues("Authorization", out var values) ? values.First() : "");
            var uri = request.RequestUri!.ToString();
            var query = request.RequestUri.Query.TrimStart('?');
            await Task.Yield();
            if (overrideResponse?.Invoke(uri, query) is { } response)
                return response;
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(responder(uri, query)),
            };
        }
    }
}
