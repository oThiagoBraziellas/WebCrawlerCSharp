using System.Collections.Concurrent;
using System.Net;
using System.Text;

namespace WebCrawler.Tests;

/// <summary>In-memory HTTP server: a path -> response map, with request logging and optional latency.</summary>
public sealed class FakeSite : HttpMessageHandler
{
    private readonly Dictionary<string, (HttpStatusCode Status, string ContentType, string Body)> _pages = new();
    public ConcurrentQueue<string> Requests { get; } = new();
    public TimeSpan Latency { get; set; } = TimeSpan.Zero;
    public int InFlight;
    public int PeakInFlight;

    public FakeSite Html(string url, string body, HttpStatusCode status = HttpStatusCode.OK) { _pages[url] = (status, "text/html; charset=utf-8", body); return this; }
    public FakeSite Page(string url, params string[] links) =>
        Html(url, $"<html><head><title>{url}</title></head><body>{string.Concat(links.Select(l => $"<a href=\"{l}\">x</a>"))}</body></html>");
    public FakeSite Raw(string url, string contentType, string body) { _pages[url] = (HttpStatusCode.OK, contentType, body); return this; }
    public FakeSite Robots(string host, string body) => Raw($"{host}/robots.txt", "text/plain", body);

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var url = request.RequestUri!.AbsoluteUri;
        Requests.Enqueue(url);
        var now = Interlocked.Increment(ref InFlight);
        int peak;
        while (now > (peak = Volatile.Read(ref PeakInFlight)))
            if (Interlocked.CompareExchange(ref PeakInFlight, now, peak) == peak) break;
        try
        {
            if (Latency > TimeSpan.Zero) await Task.Delay(Latency, cancellationToken);
            if (url.Contains("/boom")) throw new HttpRequestException("connection reset");
            if (!_pages.TryGetValue(url, out var page))
                return new HttpResponseMessage(HttpStatusCode.NotFound) { RequestMessage = request, Content = new StringContent("nope") };
            var response = new HttpResponseMessage(page.Status) { RequestMessage = request };
            response.Content = new StringContent(page.Body, Encoding.UTF8);
            response.Content.Headers.ContentType = System.Net.Http.Headers.MediaTypeHeaderValue.Parse(page.ContentType);
            return response;
        }
        finally { Interlocked.Decrement(ref InFlight); }
    }
}
