using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using System.Threading.Channels;

namespace WebCrawler;

/// <summary>
/// Polite breadth-first web crawler. The frontier is an unbounded channel consumed by a fixed number of workers
/// (bounded concurrency). Termination is detected with a pending-work counter: a worker enqueues children before
/// it decrements its own item, so the counter reaches zero only when the whole crawl is finished.
/// </summary>
public sealed class Crawler
{
    private readonly HttpClient _http;
    private readonly CrawlerOptions _options;

    public Crawler(HttpClient httpClient, CrawlerOptions? options = null)
    {
        _http = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        _options = options ?? new CrawlerOptions();
        if (_options.MaxConcurrency < 1) throw new ArgumentOutOfRangeException(nameof(options), "MaxConcurrency must be at least 1.");
        if (_options.MaxPages < 1) throw new ArgumentOutOfRangeException(nameof(options), "MaxPages must be at least 1.");
        if (_options.MaxDepth < 0) throw new ArgumentOutOfRangeException(nameof(options), "MaxDepth must not be negative.");
    }

    public Task<CrawlReport> CrawlAsync(Uri seed, CancellationToken cancellationToken = default) =>
        CrawlAsync(new[] { seed }, cancellationToken);

    public Task<CrawlReport> CrawlAsync(IEnumerable<Uri> seeds, CancellationToken cancellationToken = default) =>
        new Run(_http, _options, seeds.ToList()).ExecuteAsync(cancellationToken);

    private sealed class Run
    {
        private readonly record struct WorkItem(Uri Url, int Depth);

        private readonly HttpClient _http;
        private readonly CrawlerOptions _options;
        private readonly List<Uri> _seeds;
        private readonly HashSet<string> _allowedHosts;
        private readonly Channel<WorkItem> _frontier = Channel.CreateUnbounded<WorkItem>();
        private readonly ConcurrentDictionary<string, byte> _visited = new(StringComparer.Ordinal);
        private readonly ConcurrentDictionary<string, Lazy<Task<RobotsTxt>>> _robots = new();
        private readonly ConcurrentDictionary<string, HostSlot> _slots = new();
        private readonly ConcurrentQueue<PageResult> _results = new();
        private int _pending, _enqueued;
        private int _robotsSkips, _duplicates, _offHost, _tooDeep, _limit;

        private sealed class HostSlot { public readonly object Gate = new(); public DateTime Next = DateTime.MinValue; }

        public Run(HttpClient http, CrawlerOptions options, List<Uri> seeds)
        {
            _http = http;
            _options = options;
            _seeds = seeds;
            _allowedHosts = seeds.Select(UrlNormalizer.HostKey).ToHashSet(StringComparer.Ordinal);
        }

        public async Task<CrawlReport> ExecuteAsync(CancellationToken ct)
        {
            var clock = Stopwatch.StartNew();
            foreach (var seed in _seeds) TryEnqueue(seed, 0, isSeed: true);
            if (Volatile.Read(ref _pending) == 0) _frontier.Writer.TryComplete();

            var workers = Enumerable.Range(0, _options.MaxConcurrency).Select(_ => Task.Run(() => WorkerAsync(ct), CancellationToken.None));
            await Task.WhenAll(workers).ConfigureAwait(false);
            ct.ThrowIfCancellationRequested();

            return new CrawlReport(_results.ToList(), _robotsSkips, _duplicates, _offHost, _tooDeep, _limit, clock.Elapsed);
        }

        private async Task WorkerAsync(CancellationToken ct)
        {
            try
            {
                await foreach (var item in _frontier.Reader.ReadAllAsync(ct).ConfigureAwait(false))
                {
                    try { await ProcessAsync(item, ct).ConfigureAwait(false); }
                    finally
                    {
                        if (Interlocked.Decrement(ref _pending) == 0) _frontier.Writer.TryComplete();
                    }
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                _frontier.Writer.TryComplete();
            }
        }

        private void TryEnqueue(Uri raw, int depth, bool isSeed = false)
        {
            if (raw.Scheme is not ("http" or "https")) return;
            var url = UrlNormalizer.Normalize(raw);
            if (!isSeed && _options.SameHostOnly && !_allowedHosts.Contains(UrlNormalizer.HostKey(url))) { Interlocked.Increment(ref _offHost); return; }
            if (depth > _options.MaxDepth) { Interlocked.Increment(ref _tooDeep); return; }
            if (!_visited.TryAdd(url.AbsoluteUri, 0)) { Interlocked.Increment(ref _duplicates); return; }
            if (Interlocked.Increment(ref _enqueued) > _options.MaxPages)
            {
                Interlocked.Decrement(ref _enqueued);
                Interlocked.Increment(ref _limit);
                return;
            }
            Interlocked.Increment(ref _pending);
            _frontier.Writer.TryWrite(new WorkItem(url, depth));
        }

        private async Task ProcessAsync(WorkItem item, CancellationToken ct)
        {
            var (url, depth) = item;
            var started = Stopwatch.StartNew();

            if (_options.RespectRobots)
            {
                var robots = await GetRobotsAsync(url, ct).ConfigureAwait(false);
                if (!robots.IsAllowed(_options.RobotsToken, url.PathAndQuery))
                {
                    Interlocked.Increment(ref _robotsSkips);
                    return;
                }
                await ThrottleAsync(url, robots.GetCrawlDelay(_options.RobotsToken), ct).ConfigureAwait(false);
            }
            else
            {
                await ThrottleAsync(url, null, ct).ConfigureAwait(false);
            }

            PageResult result;
            try
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
                timeout.CancelAfter(_options.RequestTimeout);
                using var request = new HttpRequestMessage(HttpMethod.Get, url);
                request.Headers.UserAgent.ParseAdd(_options.UserAgent);
                using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token).ConfigureAwait(false);

                var finalUrl = UrlNormalizer.Normalize(response.RequestMessage?.RequestUri ?? url);
                if (finalUrl != url && !_visited.TryAdd(finalUrl.AbsoluteUri, 0))
                {
                    // The redirect target is already owned by another work item: do not report or parse it twice.
                    Interlocked.Increment(ref _duplicates);
                    return;
                }

                var status = (int)response.StatusCode;
                var mediaType = response.Content.Headers.ContentType?.MediaType;
                if (!response.IsSuccessStatusCode)
                {
                    result = new PageResult(finalUrl, depth, status, null, Array.Empty<Uri>(), $"HTTP {status}", 0, started.Elapsed);
                }
                else if (mediaType is null || !(mediaType.Equals("text/html", StringComparison.OrdinalIgnoreCase) || mediaType.Equals("application/xhtml+xml", StringComparison.OrdinalIgnoreCase)))
                {
                    result = new PageResult(finalUrl, depth, status, null, Array.Empty<Uri>(), null, response.Content.Headers.ContentLength ?? 0, started.Elapsed);
                }
                else
                {
                    var (html, bytes) = await ReadBodyAsync(response, timeout.Token).ConfigureAwait(false);
                    var links = HtmlParsing.ExtractLinks(html, finalUrl);
                    result = new PageResult(finalUrl, depth, status, HtmlParsing.ExtractTitle(html), links, null, bytes, started.Elapsed);
                    foreach (var link in links) TryEnqueue(link, depth + 1);
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException or IOException)
            {
                var message = ex is OperationCanceledException ? "Timeout" : ex.Message;
                result = new PageResult(url, depth, null, null, Array.Empty<Uri>(), message, 0, started.Elapsed);
            }

            _results.Enqueue(result);
            try { _options.OnPage?.Invoke(result); } catch { /* user callback must not break the crawl */ }
        }

        private async Task<(string Html, long Bytes)> ReadBodyAsync(HttpResponseMessage response, CancellationToken ct)
        {
            await using var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            var buffer = new byte[8192];
            using var ms = new MemoryStream();
            int read;
            while (ms.Length < _options.MaxBodyBytes && (read = await stream.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0)
                ms.Write(buffer, 0, read);
            var charset = response.Content.Headers.ContentType?.CharSet;
            Encoding encoding;
            try { encoding = charset is null ? Encoding.UTF8 : Encoding.GetEncoding(charset.Trim('"')); }
            catch (ArgumentException) { encoding = Encoding.UTF8; }
            return (encoding.GetString(ms.GetBuffer(), 0, (int)ms.Length), ms.Length);
        }

        private Task<RobotsTxt> GetRobotsAsync(Uri url, CancellationToken ct)
        {
            var key = UrlNormalizer.HostKey(url);
            // The first caller's token drives the fetch; Lazy guarantees a single fetch per host.
            return _robots.GetOrAdd(key, k => new Lazy<Task<RobotsTxt>>(() => FetchRobotsAsync(k, ct))).Value;
        }

        private async Task<RobotsTxt> FetchRobotsAsync(string hostKey, CancellationToken ct)
        {
            try
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
                timeout.CancelAfter(_options.RequestTimeout);
                using var request = new HttpRequestMessage(HttpMethod.Get, hostKey + "/robots.txt");
                request.Headers.UserAgent.ParseAdd(_options.UserAgent);
                using var response = await _http.SendAsync(request, timeout.Token).ConfigureAwait(false);
                var status = (int)response.StatusCode;
                if (response.IsSuccessStatusCode) return RobotsTxt.Parse(await response.Content.ReadAsStringAsync(timeout.Token).ConfigureAwait(false));
                // 4xx: no robots file, everything is allowed. 5xx: server trouble, be conservative.
                return status is >= 400 and < 500 ? RobotsTxt.AllowAll : RobotsTxt.DisallowAll;
            }
            catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException or IOException)
            {
                ct.ThrowIfCancellationRequested();
                return RobotsTxt.DisallowAll;
            }
        }

        private async Task ThrottleAsync(Uri url, TimeSpan? crawlDelay, CancellationToken ct)
        {
            var delay = crawlDelay is { } d && d > _options.PerHostDelay ? d : _options.PerHostDelay;
            if (delay <= TimeSpan.Zero) return;
            var slot = _slots.GetOrAdd(UrlNormalizer.HostKey(url), _ => new HostSlot());
            TimeSpan wait;
            lock (slot.Gate)
            {
                var now = DateTime.UtcNow;
                var start = slot.Next > now ? slot.Next : now;
                slot.Next = start + delay;
                wait = start - now;
            }
            if (wait > TimeSpan.Zero) await Task.Delay(wait, ct).ConfigureAwait(false);
        }
    }
}
