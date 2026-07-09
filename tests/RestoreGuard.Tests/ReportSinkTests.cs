using System.Net;
using RestoreGuard.Cli;

namespace RestoreGuard.Tests;

public class AwsSigV4Tests
{
    // The canonical example from the AWS SigV4 documentation (iam ListUsers,
    // 2015-08-30) — the signature is a published constant, so a passing test
    // means the whole canonicalization + key-derivation chain is right.
    [Fact]
    public void AuthorizationHeader_MatchesAwsDocumentedExample()
    {
        var header = AwsSigV4.AuthorizationHeader(
            method: "GET",
            canonicalPath: "/",
            canonicalQuery: "Action=ListUsers&Version=2010-05-08",
            headers: new Dictionary<string, string>
            {
                ["content-type"] = "application/x-www-form-urlencoded; charset=utf-8",
                ["host"] = "iam.amazonaws.com",
                ["x-amz-date"] = "20150830T123600Z",
            },
            payloadSha256Hex: AwsSigV4.Sha256Hex([]),
            now: new DateTimeOffset(2015, 8, 30, 12, 36, 0, TimeSpan.Zero),
            region: "us-east-1", service: "iam",
            accessKey: "AKIDEXAMPLE", secretKey: "wJalrXUtnFEMI/K7MDENG+bPxRfiCYEXAMPLEKEY");

        Assert.Equal(
            "AWS4-HMAC-SHA256 Credential=AKIDEXAMPLE/20150830/us-east-1/iam/aws4_request, "
            + "SignedHeaders=content-type;host;x-amz-date, "
            + "Signature=5d672d79c15b13162d9279b0855cfba6789a8edb4c82c400e06b5924a6f2b5d7",
            header);
    }

    [Fact]
    public void UriEncode_KeepsUnreserved_EncodesTheRestUppercase()
    {
        Assert.Equal("rg-report-20260709T030026Z.json", AwsSigV4.UriEncode("rg-report-20260709T030026Z.json"));
        Assert.Equal("a%20b%2Fc%C3%A9", AwsSigV4.UriEncode("a b/cé"));
    }
}

public class FolderReportSinkTests : IDisposable
{
    private readonly DirectoryInfo _dir = Directory.CreateTempSubdirectory("rg-sink-test");

    public void Dispose() => _dir.Delete(recursive: true);

    [Fact]
    public async Task Publish_WritesTimestampedReportAndLatest_NoTempLeftovers()
    {
        var sink = new FolderReportSink(_dir.FullName, keepLast: null);

        await sink.PublishAsync("""{"n":1}""", new DateTimeOffset(2026, 7, 9, 3, 0, 26, TimeSpan.Zero));
        var detail = await sink.PublishAsync("""{"n":2}""", new DateTimeOffset(2026, 7, 10, 3, 0, 26, TimeSpan.Zero));

        Assert.Contains("rg-report-20260710T030026Z.json", detail);
        var files = Directory.GetFiles(_dir.FullName).Select(Path.GetFileName).Order().ToList();
        Assert.Equal(["latest.json", "rg-report-20260709T030026Z.json", "rg-report-20260710T030026Z.json"], files);
        // latest.json always mirrors the newest report.
        Assert.Equal("""{"n":2}""", File.ReadAllText(Path.Combine(_dir.FullName, "latest.json")));
    }

    [Fact]
    public async Task Publish_WithKeepLast_PrunesOldestReports_NeverLatest()
    {
        var sink = new FolderReportSink(_dir.FullName, keepLast: 2);
        for (var day = 1; day <= 4; day++)
            await sink.PublishAsync($$"""{"day":{{day}}}""", new DateTimeOffset(2026, 7, day, 3, 0, 0, TimeSpan.Zero));

        var files = Directory.GetFiles(_dir.FullName).Select(Path.GetFileName).Order().ToList();
        Assert.Equal(["latest.json", "rg-report-20260703T030000Z.json", "rg-report-20260704T030000Z.json"], files);
    }

    [Fact]
    public async Task Verify_LeavesNoTraceBehind()
    {
        var folder = Path.Combine(_dir.FullName, "made-by-verify");
        await new FolderReportSink(folder, keepLast: null).VerifyAsync();

        Assert.True(Directory.Exists(folder));
        Assert.Empty(Directory.GetFiles(folder));
    }
}

internal sealed class RecordingHandler : HttpMessageHandler
{
    public readonly List<(HttpMethod Method, Uri Uri, string Authorization, string ContentSha, string Body)> Requests = [];
    public HttpStatusCode Status { get; set; } = HttpStatusCode.OK;

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        Requests.Add((
            request.Method,
            request.RequestUri!,
            request.Headers.TryGetValues("Authorization", out var auth) ? auth.Single() : "",
            request.Headers.TryGetValues("x-amz-content-sha256", out var sha) ? sha.Single() : "",
            request.Content is null ? "" : await request.Content.ReadAsStringAsync(ct)));
        return new HttpResponseMessage(Status) { Content = new StringContent("AccessDenied or whatever") };
    }
}

public class S3ReportSinkTests
{
    private static readonly DateTimeOffset FixedNow = new(2026, 7, 9, 3, 0, 26, TimeSpan.Zero);

    private static (S3ReportSink Sink, RecordingHandler Http) Make(S3SinkConfig config)
    {
        var handler = new RecordingHandler();
        return (new S3ReportSink(config, configDir: ".", handler, () => FixedNow), handler);
    }

    [Fact]
    public async Task Publish_PathStyle_PutsTimestampedKeyAndLatest_Signed()
    {
        var (sink, http) = Make(new S3SinkConfig(
            "http://192.168.1.10:9000", "backups", Prefix: "restoreguard/",
            AccessKey: "minio", SecretKey: "minio-secret"));

        var json = """{"overall":"green"}""";
        await sink.PublishAsync(json, FixedNow);

        Assert.Equal(2, http.Requests.Count);
        var (method, uri, authorization, contentSha, body) = http.Requests[0];
        Assert.Equal(HttpMethod.Put, method);
        Assert.Equal("http://192.168.1.10:9000/backups/restoreguard/rg-report-20260709T030026Z.json", uri.ToString());
        Assert.Equal(json, body);
        Assert.Equal(AwsSigV4.Sha256Hex(System.Text.Encoding.UTF8.GetBytes(json)), contentSha);
        Assert.StartsWith("AWS4-HMAC-SHA256 Credential=minio/20260709/us-east-1/s3/aws4_request", authorization);
        Assert.Contains("SignedHeaders=host;x-amz-content-sha256;x-amz-date", authorization);

        Assert.Equal("http://192.168.1.10:9000/backups/restoreguard/latest.json", http.Requests[1].Uri.ToString());
    }

    [Fact]
    public async Task Publish_VirtualHostStyle_PutsBucketInHostname()
    {
        var (sink, http) = Make(new S3SinkConfig(
            "https://s3.amazonaws.com", "my-reports", Prefix: "", ForcePathStyle: false,
            AccessKey: "AKID", SecretKey: "secret"));

        await sink.PublishAsync("{}", FixedNow);

        Assert.Equal("https://my-reports.s3.amazonaws.com/rg-report-20260709T030026Z.json",
            http.Requests[0].Uri.ToString());
    }

    [Fact]
    public async Task Verify_PutsThenDeletesProbeObject()
    {
        var (sink, http) = Make(new S3SinkConfig(
            "http://192.168.1.10:9000", "backups", AccessKey: "k", SecretKey: "s"));

        await sink.VerifyAsync();

        Assert.Equal(
            [(HttpMethod.Put, "/backups/restoreguard/.rg-write-test"), (HttpMethod.Delete, "/backups/restoreguard/.rg-write-test")],
            http.Requests.Select(r => (r.Method, r.Uri.AbsolutePath)).ToList());
    }

    [Fact]
    public async Task Publish_NonSuccessStatus_ThrowsWithStatusAndBody()
    {
        var (sink, http) = Make(new S3SinkConfig(
            "http://192.168.1.10:9000", "backups", AccessKey: "k", SecretKey: "s"));
        http.Status = HttpStatusCode.Forbidden;

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => sink.PublishAsync("{}", FixedNow));
        Assert.Contains("403", ex.Message);
        Assert.Contains("AccessDenied", ex.Message);
    }

    [Fact]
    public async Task SecretFiles_ResolveRelativeToConfigDir()
    {
        var dir = Directory.CreateTempSubdirectory("rg-s3-secrets");
        try
        {
            File.WriteAllText(Path.Combine(dir.FullName, "ak"), "file-access-key\n");
            File.WriteAllText(Path.Combine(dir.FullName, "sk"), "file-secret-key\n");
            var handler = new RecordingHandler();
            var sink = new S3ReportSink(
                new S3SinkConfig("http://h:9000", "b", AccessKeyFile: "ak", SecretKeyFile: "sk"),
                dir.FullName, handler, () => FixedNow);

            await sink.PublishAsync("{}", FixedNow);

            Assert.Contains("Credential=file-access-key/", handler.Requests[0].Authorization);
        }
        finally
        {
            dir.Delete(recursive: true);
        }
    }
}
