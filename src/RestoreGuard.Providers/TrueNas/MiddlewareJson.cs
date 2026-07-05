using System.Text.Json;

namespace RestoreGuard.Providers.TrueNas;

/// <summary>Helpers for TrueNAS middleware JSON conventions.</summary>
internal static class MiddlewareJson
{
    /// <summary>Timestamps are extended-JSON objects: {"$date": &lt;unix ms&gt;}.</summary>
    public static DateTimeOffset? GetDate(JsonElement parent, string property)
    {
        if (!parent.TryGetProperty(property, out var el) || el.ValueKind != JsonValueKind.Object)
            return null;
        if (!el.TryGetProperty("$date", out var date))
            return null;
        return DateTimeOffset.FromUnixTimeMilliseconds(date.GetInt64());
    }

    /// <summary>Sizes are property objects: {"parsed": &lt;bytes&gt;, "rawvalue": …}.</summary>
    public static long GetParsedSize(JsonElement parent, string property)
    {
        if (!parent.TryGetProperty(property, out var el) || el.ValueKind != JsonValueKind.Object)
            return 0;
        return el.TryGetProperty("parsed", out var parsed) && parsed.ValueKind == JsonValueKind.Number
            ? parsed.GetInt64()
            : 0;
    }
}
