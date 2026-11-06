using System.Net;
using System.Net.Sockets;
using System.Text;

namespace WebCrawler.Tests;

/// <summary>End-to-end tests against a real in-process HTTP server (loopback only, no internet).</summary>
public sealed class LocalServer : IDisposable
{
    private readonly HttpListener _listener = new();
    private readonly CancellationTokenSource _cts = new();
    private readonly Task _loop;
    public Uri BaseUri { get; }
    public List<string> Hits { get; } = new();

    public LocalServer(Func<string, (int Status, string Type, string Body, string? Location)> handler)
    {
        var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        var port = ((IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();
        BaseUri = new Uri($"http://127.0.0.1:{port}");
        _listener.Prefixes.Add(BaseUri + "");
        _listener.Start();
        _loop = Task.Run(async () =>
        {
            while (!_cts.IsCancellationRequested)
            {
                HttpListenerContext ctx;
                try { ctx = await _listener.GetContextAsync(); }
                catch { break; }
                var path = ctx.Request.Url!.PathAndQuery;
                lock (Hits) Hits.Add(path);
                var (status, type, body, location) = handler(path);
                ctx.Response.StatusCode = status;
                ctx.Response.ContentType = type;
                if (location is not null) ctx.Response.RedirectLocation = location;
                var bytes = Encoding.UTF8.GetBytes(body);
                await ctx.Response.OutputStream.WriteAsync(bytes);
                ctx.Response.Close();
            }
        });
    }

    public void Dispose()
    {
        _cts.Cancel();
        _listener.Close();
        try { _loop.Wait(2000); } catch { }
    }
}

public class HttpListenerTests
{
    [Fact]
    public async Task CrawlsRealHttpServer_FollowingRedirects_AndRobots()
    {
        using var server = new LocalServer(path => path switch
        {
            "/robots.txt" => (200, "text/plain", "User-agent: *\nDisallow: /secret", null),
            "/" => (200, "text/html", "<title>Home</title><a href='/old'>old</a><a href='/secret'>s</a><a href='/page'>p</a>", null),
            "/old" => (301, "text/html", "", "/page"),
            "/page" => (200, "text/html", "<title>Page</title><a href='/'>home</a>", null),
            "/secret" => (200, "text/html", "<title>Secret</title>", null),
            _ => (404, "text/plain", "not found", null)
        });

        using var http = new HttpClient(new SocketsHttpHandler { AllowAutoRedirect = true });
        var report = await new Crawler(http, new CrawlerOptions { UserAgent = "TestBot/1.0" }).CrawlAsync(server.BaseUri);

        var paths = report.Pages.Select(p => p.Url.AbsolutePath).OrderBy(x => x).ToArray();
        Assert.Equal(new[] { "/", "/page" }, paths);
        Assert.Equal("Home", report.Pages.First(p => p.Url.AbsolutePath == "/").Title);
        Assert.Equal(1, report.SkippedByRobots);
        lock (server.Hits)
        {
            Assert.DoesNotContain("/secret", server.Hits);
            Assert.Contains("/old", server.Hits); // the redirect was followed, but /page is reported only once above
        }
    }

    [Fact]
    public async Task ReportsConnectionFailure()
    {
        var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        var port = ((IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop(); // nothing listens on this port now
        using var http = new HttpClient();
        var report = await new Crawler(http, new CrawlerOptions { RespectRobots = false }).CrawlAsync(new Uri($"http://127.0.0.1:{port}/"));
        var page = Assert.Single(report.Pages);
        Assert.NotNull(page.Error);
        Assert.False(page.Success);
    }
}
