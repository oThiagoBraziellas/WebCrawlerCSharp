namespace WebCrawler;

public sealed record CrawlerOptions
{
    public string UserAgent { get; init; } = "PortfolioCrawler/1.0";
    public int MaxDepth { get; init; } = 3;
    public int MaxPages { get; init; } = 100;
    /// <summary>Number of pages fetched at the same time.</summary>
    public int MaxConcurrency { get; init; } = 4;
    /// <summary>Only follow links on the same host as one of the seeds.</summary>
    public bool SameHostOnly { get; init; } = true;
    public bool RespectRobots { get; init; } = true;
    /// <summary>Minimum delay between two requests to the same host. robots.txt Crawl-delay can raise it.</summary>
    public TimeSpan PerHostDelay { get; init; } = TimeSpan.Zero;
    public TimeSpan RequestTimeout { get; init; } = TimeSpan.FromSeconds(15);
    /// <summary>Response bodies larger than this are truncated (links beyond the cut are lost).</summary>
    public int MaxBodyBytes { get; init; } = 2 * 1024 * 1024;
    /// <summary>Called as each page finishes, possibly from several threads concurrently.</summary>
    public Action<PageResult>? OnPage { get; init; }

    internal string RobotsToken => UserAgent.Split('/')[0].Trim();
}

public sealed record PageResult(
    Uri Url,
    int Depth,
    int? StatusCode,
    string? Title,
    IReadOnlyList<Uri> Links,
    string? Error,
    long Bytes,
    TimeSpan Elapsed)
{
    public bool Success => Error is null && StatusCode is >= 200 and < 300;
}

public sealed record CrawlReport(
    IReadOnlyList<PageResult> Pages,
    int SkippedByRobots,
    int SkippedDuplicates,
    int SkippedOffHost,
    int SkippedTooDeep,
    int SkippedPageLimit,
    TimeSpan Elapsed);
