using System.Net;

namespace WebCrawler.Tests;

public class CrawlerTests
{
    private const string Host = "https://site.test";

    private static async Task<CrawlReport> Crawl(FakeSite site, CrawlerOptions? options = null, string start = "/")
    {
        using var http = new HttpClient(site);
        return await new Crawler(http, options ?? new CrawlerOptions()).CrawlAsync(new Uri(Host + start));
    }

    private static string[] Urls(CrawlReport r) => r.Pages.Select(p => p.Url.AbsolutePath).OrderBy(x => x, StringComparer.Ordinal).ToArray();

    [Fact]
    public async Task CrawlsLinkedPages_AndExtractsTitles()
    {
        var site = new FakeSite()
            .Page($"{Host}/", "/a", "/b")
            .Page($"{Host}/a", "/c")
            .Page($"{Host}/b")
            .Page($"{Host}/c");
        var report = await Crawl(site);
        Assert.Equal(new[] { "/", "/a", "/b", "/c" }, Urls(report));
        Assert.All(report.Pages, p => Assert.True(p.Success));
        Assert.Equal($"{Host}/a", report.Pages.First(p => p.Url.AbsolutePath == "/a").Title);
        Assert.Equal(2, report.Pages.First(p => p.Url.AbsolutePath == "/c").Depth);
    }

    [Fact]
    public async Task Cycles_AndDuplicateLinks_AreFetchedOnce()
    {
        var site = new FakeSite()
            .Page($"{Host}/", "/a", "/a#section", "/A/../a", "/b")
            .Page($"{Host}/a", "/", "/b", "/a")
            .Page($"{Host}/b", "/", "/a");
        var report = await Crawl(site);
        Assert.Equal(3, report.Pages.Count);
        Assert.Equal(3, site.Requests.Count(r => !r.EndsWith("robots.txt")));
        Assert.True(report.SkippedDuplicates >= 4);
    }

    [Fact]
    public async Task MaxDepth_LimitsTraversal()
    {
        var site = new FakeSite().Page($"{Host}/", "/1").Page($"{Host}/1", "/2").Page($"{Host}/2", "/3").Page($"{Host}/3");
        var report = await Crawl(site, new CrawlerOptions { MaxDepth = 1 });
        Assert.Equal(new[] { "/", "/1" }, Urls(report));
        Assert.Equal(1, report.SkippedTooDeep);
    }

    [Fact]
    public async Task MaxPages_IsNeverExceeded()
    {
        var links = Enumerable.Range(0, 50).Select(i => $"/p{i}").ToArray();
        var site = new FakeSite().Page($"{Host}/", links);
        foreach (var l in links) site.Page(Host + l);
        var report = await Crawl(site, new CrawlerOptions { MaxPages = 10, MaxConcurrency = 8 });
        Assert.Equal(10, report.Pages.Count);
        Assert.Equal(41, report.SkippedPageLimit);
    }

    [Fact]
    public async Task SameHostOnly_IgnoresExternalLinks()
    {
        var site = new FakeSite().Page($"{Host}/", "https://other.test/x", "/local").Page($"{Host}/local").Page("https://other.test/x");
        var report = await Crawl(site);
        Assert.Equal(new[] { "/", "/local" }, Urls(report));
        Assert.Equal(1, report.SkippedOffHost);

        site.Requests.Clear();
        var open = await Crawl(site, new CrawlerOptions { SameHostOnly = false });
        Assert.Equal(3, open.Pages.Count);
    }

    [Fact]
    public async Task Robots_DisallowedPathsAreNotFetched()
    {
        var site = new FakeSite()
            .Robots(Host, "User-agent: *\nDisallow: /admin\nAllow: /admin/public")
            .Page($"{Host}/", "/admin", "/admin/public", "/ok")
            .Page($"{Host}/admin").Page($"{Host}/admin/public").Page($"{Host}/ok");
        var report = await Crawl(site);
        Assert.Equal(new[] { "/", "/admin/public", "/ok" }, Urls(report));
        Assert.Equal(1, report.SkippedByRobots);
        Assert.DoesNotContain($"{Host}/admin", site.Requests);
    }

    [Fact]
    public async Task Robots_IsFetchedOncePerHost()
    {
        var site = new FakeSite().Page($"{Host}/", "/a", "/b", "/c").Page($"{Host}/a").Page($"{Host}/b").Page($"{Host}/c");
        await Crawl(site, new CrawlerOptions { MaxConcurrency = 4 });
        Assert.Equal(1, site.Requests.Count(r => r.EndsWith("/robots.txt")));
    }

    [Fact]
    public async Task Robots_CanBeIgnored()
    {
        var site = new FakeSite().Robots(Host, "User-agent: *\nDisallow: /").Page($"{Host}/");
        Assert.Empty((await Crawl(site)).Pages);
        var report = await Crawl(site, new CrawlerOptions { RespectRobots = false });
        Assert.Single(report.Pages);
    }

    [Fact]
    public async Task Robots_ServerError_IsTreatedAsDisallowAll()
    {
        var site = new FakeSite().Html($"{Host}/robots.txt", "oops", HttpStatusCode.InternalServerError).Page($"{Host}/");
        var report = await Crawl(site);
        Assert.Empty(report.Pages);
        Assert.Equal(1, report.SkippedByRobots);
    }

    [Fact]
    public async Task Concurrency_IsBounded_ButActuallyParallel()
    {
        var links = Enumerable.Range(0, 30).Select(i => $"/p{i}").ToArray();
        var site = new FakeSite { Latency = TimeSpan.FromMilliseconds(30) }.Page($"{Host}/", links);
        foreach (var l in links) site.Page(Host + l);
        var report = await Crawl(site, new CrawlerOptions { MaxConcurrency = 5, RespectRobots = false });
        Assert.Equal(31, report.Pages.Count);
        Assert.InRange(site.PeakInFlight, 2, 5);
    }

    [Fact]
    public async Task Errors_AreReported_WithoutStoppingTheCrawl()
    {
        var site = new FakeSite()
            .Page($"{Host}/", "/missing", "/boom", "/broken", "/fine")
            .Html($"{Host}/broken", "x", HttpStatusCode.BadGateway)
            .Page($"{Host}/fine");
        var report = await Crawl(site);
        var byPath = report.Pages.ToDictionary(p => p.Url.AbsolutePath);
        Assert.Equal(404, byPath["/missing"].StatusCode);
        Assert.Equal("connection reset", byPath["/boom"].Error);
        Assert.Null(byPath["/boom"].StatusCode);
        Assert.Equal(502, byPath["/broken"].StatusCode);
        Assert.True(byPath["/fine"].Success);
        Assert.False(byPath["/missing"].Success);
    }

    [Fact]
    public async Task NonHtmlContent_IsRecordedButNotParsed()
    {
        var site = new FakeSite()
            .Page($"{Host}/", "/data.json", "/img")
            .Raw($"{Host}/data.json", "application/json", """{"a":"<a href='/hidden'>x</a>"}""")
            .Raw($"{Host}/img", "image/png", "binary");
        var report = await Crawl(site);
        Assert.Equal(new[] { "/", "/data.json", "/img" }, Urls(report));
        Assert.DoesNotContain(site.Requests, r => r.EndsWith("/hidden"));
    }

    [Fact]
    public async Task Timeout_IsReportedAsError()
    {
        var site = new FakeSite { Latency = TimeSpan.FromSeconds(5) }.Page($"{Host}/");
        var report = await Crawl(site, new CrawlerOptions { RequestTimeout = TimeSpan.FromMilliseconds(50), RespectRobots = false });
        Assert.Equal("Timeout", report.Pages.Single().Error);
    }

    [Fact]
    public async Task Cancellation_Aborts()
    {
        var site = new FakeSite { Latency = TimeSpan.FromSeconds(5) }.Page($"{Host}/");
        using var http = new HttpClient(site);
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            new Crawler(http, new CrawlerOptions { RespectRobots = false }).CrawlAsync(new Uri(Host + "/"), cts.Token));
    }

    [Fact]
    public async Task PerHostDelay_SpacesRequests()
    {
        var site = new FakeSite().Page($"{Host}/", "/a", "/b").Page($"{Host}/a").Page($"{Host}/b");
        var report = await Crawl(site, new CrawlerOptions { PerHostDelay = TimeSpan.FromMilliseconds(100), RespectRobots = false, MaxConcurrency = 4 });
        Assert.Equal(3, report.Pages.Count);
        Assert.True(report.Elapsed >= TimeSpan.FromMilliseconds(190), report.Elapsed.ToString());
    }

    [Fact]
    public async Task OnPage_CallbackReceivesEveryPage_EvenIfItThrows()
    {
        var seen = 0;
        var site = new FakeSite().Page($"{Host}/", "/a").Page($"{Host}/a");
        var report = await Crawl(site, new CrawlerOptions { OnPage = _ => { Interlocked.Increment(ref seen); throw new Exception("cb"); } });
        Assert.Equal(2, seen);
        Assert.Equal(2, report.Pages.Count);
    }

    [Fact]
    public async Task MultipleSeeds_AreAllCrawled()
    {
        var site = new FakeSite().Page($"{Host}/", "/a").Page($"{Host}/a").Page("https://two.test/", "/b").Page("https://two.test/b");
        using var http = new HttpClient(site);
        var report = await new Crawler(http).CrawlAsync(new[] { new Uri(Host + "/"), new Uri("https://two.test/") });
        Assert.Equal(4, report.Pages.Count);
    }

    [Fact]
    public async Task NoSeeds_CompletesImmediately()
    {
        using var http = new HttpClient(new FakeSite());
        var report = await new Crawler(http).CrawlAsync(Array.Empty<Uri>());
        Assert.Empty(report.Pages);
    }

    [Fact]
    public void InvalidOptions_Throw()
    {
        using var http = new HttpClient(new FakeSite());
        Assert.Throws<ArgumentOutOfRangeException>(() => new Crawler(http, new CrawlerOptions { MaxConcurrency = 0 }));
        Assert.Throws<ArgumentOutOfRangeException>(() => new Crawler(http, new CrawlerOptions { MaxPages = 0 }));
        Assert.Throws<ArgumentOutOfRangeException>(() => new Crawler(http, new CrawlerOptions { MaxDepth = -1 }));
    }
}
