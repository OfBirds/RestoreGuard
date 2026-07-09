using System.Security.Cryptography;
using System.Text;

namespace RestoreGuard.Cli;

/// <summary>
/// Minimal AWS Signature V4 signer — just enough for object PUT/DELETE against any
/// S3-compatible endpoint (MinIO, Garage, R2, AWS). Hand-rolled on purpose: the
/// report sink only ever puts and deletes single objects, and a full SDK would be
/// the largest dependency in the binary by far.
/// </summary>
public static class AwsSigV4
{
    /// <summary>
    /// Builds the Authorization header value. <paramref name="canonicalPath"/> and
    /// <paramref name="canonicalQuery"/> must already be in canonical form (path
    /// segments RFC3986-encoded — see <see cref="UriEncode"/> — and query pairs
    /// encoded and sorted; empty string for no query). <paramref name="headers"/>
    /// must contain every header to sign, including host and x-amz-date, with
    /// x-amz-date formatted from the same <paramref name="now"/>.
    /// </summary>
    public static string AuthorizationHeader(
        string method, string canonicalPath, string canonicalQuery,
        IReadOnlyDictionary<string, string> headers, string payloadSha256Hex,
        DateTimeOffset now, string region, string service, string accessKey, string secretKey)
    {
        var amzDate = AmzDate(now);
        var dateStamp = now.UtcDateTime.ToString("yyyyMMdd");

        var sorted = headers
            .Select(h => (Name: h.Key.ToLowerInvariant(), Value: h.Value.Trim()))
            .OrderBy(h => h.Name, StringComparer.Ordinal)
            .ToList();
        var canonicalHeaders = string.Concat(sorted.Select(h => $"{h.Name}:{h.Value}\n"));
        var signedHeaders = string.Join(';', sorted.Select(h => h.Name));

        var canonicalRequest = string.Join('\n',
            method, canonicalPath, canonicalQuery, canonicalHeaders, signedHeaders, payloadSha256Hex);

        var scope = $"{dateStamp}/{region}/{service}/aws4_request";
        var stringToSign = string.Join('\n',
            "AWS4-HMAC-SHA256", amzDate, scope, Sha256Hex(Encoding.UTF8.GetBytes(canonicalRequest)));

        var kDate = HmacSha256(Encoding.UTF8.GetBytes("AWS4" + secretKey), dateStamp);
        var kRegion = HmacSha256(kDate, region);
        var kService = HmacSha256(kRegion, service);
        var kSigning = HmacSha256(kService, "aws4_request");
        var signature = Convert.ToHexStringLower(HmacSha256(kSigning, stringToSign));

        return $"AWS4-HMAC-SHA256 Credential={accessKey}/{scope}, SignedHeaders={signedHeaders}, Signature={signature}";
    }

    public static string AmzDate(DateTimeOffset now) => now.UtcDateTime.ToString("yyyyMMdd'T'HHmmss'Z'");

    public static string Sha256Hex(byte[] payload) => Convert.ToHexStringLower(SHA256.HashData(payload));

    /// <summary>RFC3986 encoding as SigV4 wants it: unreserved characters pass,
    /// everything else becomes uppercase %XX (UTF-8 bytes). Encode each path
    /// segment separately — '/' itself is never encoded in an object key path.</summary>
    public static string UriEncode(string segment)
    {
        var sb = new StringBuilder(segment.Length);
        foreach (var b in Encoding.UTF8.GetBytes(segment))
        {
            var c = (char)b;
            if (c is (>= 'A' and <= 'Z') or (>= 'a' and <= 'z') or (>= '0' and <= '9') or '-' or '.' or '_' or '~')
                sb.Append(c);
            else
                sb.Append($"%{b:X2}");
        }
        return sb.ToString();
    }

    private static byte[] HmacSha256(byte[] key, string data) =>
        HMACSHA256.HashData(key, Encoding.UTF8.GetBytes(data));
}
