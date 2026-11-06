using WebCrawler;

// Usage: webcrawler <url> [--depth n] [--max-pages n] [--concurrency n] [--delay-ms n] [--any-host] [--no-robots]
if (args.Length == 0 || !Uri.TryCreate(args[0], UriKind.Absolute, out var seed) || seed.Scheme is not ("http" or "https"))
{
    Console.Error.WriteLine("usage: webcrawler <http(s)-url> [--depth n] [--max-pages n] [--concurrency n] [--delay-ms n] [--any-host] [--no-robots]");
    return 2;
}

int Int(string name, int fallback)
{
    var i = Array.IndexOf(args, name);
    return i >= 0 && i + 1 < args.Length && int.TryParse(args[i + 1], out var v) ? v : fallback;
}

var options = new CrawlerOptions
{
    MaxDepth = Int("--depth", 2),
    MaxPages = Int("--max-pages", 50),
    MaxConcurrency = Int("--concurrency", 4),
    PerHostDelay = TimeSpan.FromMilliseconds(Int("--delay-ms", 0)),
    SameHostOnly = !args.Contains("--any-host"),
    RespectRobots = !args.Contains("--no-robots"),
    OnPage = p => Console.WriteLine($"{(p.StatusCode?.ToString() ?? "ERR"),3} d={p.Depth} {p.Url}  {p.Title ?? p.Error}")
};

using var cts = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };

using var http = new HttpClient(new SocketsHttpHandler { AllowAutoRedirect = true, MaxAutomaticRedirections = 5 });
try
{
    var report = await new Crawler(http, options).CrawlAsync(seed, cts.Token);
    Console.WriteLine($"\n{report.Pages.Count} pages in {report.Elapsed.TotalSeconds:F2}s | " +
                      $"robots-skipped={report.SkippedByRobots} duplicates={report.SkippedDuplicates} off-host={report.SkippedOffHost} " +
                      $"too-deep={report.SkippedTooDeep} over-limit={report.SkippedPageLimit}");
    return 0;
}
catch (OperationCanceledException)
{
    Console.Error.WriteLine("cancelled");
    return 130;
}
