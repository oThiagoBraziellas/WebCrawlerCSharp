namespace WebCrawler;

/// <summary>Canonicalises URLs so equivalent addresses are deduplicated.</summary>
public static class UrlNormalizer
{
    /// <summary>Lowercases scheme/host, drops fragment and default port, and maps an empty path to "/".</summary>
    public static Uri Normalize(Uri uri)
    {
        var builder = new UriBuilder(uri)
        {
            Fragment = string.Empty,
            Host = uri.Host.ToLowerInvariant(),
            Scheme = uri.Scheme.ToLowerInvariant()
        };
        if (builder.Path.Length == 0) builder.Path = "/";
        if (uri.IsDefaultPort) builder.Port = -1;
        return builder.Uri;
    }

    /// <summary>Key used for host-level state (robots.txt cache, politeness): scheme://host:port.</summary>
    public static string HostKey(Uri uri) => uri.GetLeftPart(UriPartial.Authority).ToLowerInvariant();
}
