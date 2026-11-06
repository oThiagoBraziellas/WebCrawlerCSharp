# WebCrawler em C#

Rastreador web (*crawler*) assíncrono e educado em C# (.NET 8): busca em largura a partir de uma ou mais URLs, com **concorrência limitada via `System.Threading.Channels`**, respeito ao **robots.txt** (padrão RFC 9309), deduplicação por URL normalizada, limites de profundidade e de páginas, atraso por host e cancelamento. Não usa bibliotecas de terceiros: o HTML é analisado por expressões regulares pré-compiladas (source generators do .NET).

## Por que este projeto

Crawlers são um clássico de system design (Google, Bing, Common Crawl, arquivos web) e juntam, em um só problema, vários tópicos cobrados em entrevistas: fronteira de URLs, deduplicação, *back-pressure*, detecção de término em um sistema concorrente, cortesia com os servidores (robots.txt e *rate limiting* por host), tratamento de falhas de rede e limites de memória. O projeto também mostra como testar código de rede sem internet: um `HttpMessageHandler` falso e um servidor `HttpListener` em *loopback*.

## Recursos

- Concorrência limitada (`MaxConcurrency`) com fronteira em `Channel<T>` e workers assíncronos.
- Detecção de término por contador de trabalho pendente (sem *polling* nem timeouts).
- `RobotsTxt`: grupos por user-agent (o mais específico vence), regra mais longa vence, *Allow* ganha empates, curingas `*` e âncora `$`, `Crawl-delay`. Um arquivo por host, baixado uma única vez; 4xx libera tudo, 5xx bloqueia tudo.
- Normalização de URL (esquema/host em minúsculas, remove fragmento e porta padrão) e deduplicação, inclusive de destinos de redirecionamento.
- Limites: `MaxDepth`, `MaxPages` (nunca excedido, mesmo com vários workers), `MaxBodyBytes`, `RequestTimeout`.
- Cortesia: `PerHostDelay` e `Crawl-delay` do robots.txt.
- Apenas `text/html` é analisado; `rel="nofollow"`, `mailto:`, `javascript:` e âncoras internas são ignorados.
- Relatório com páginas (status, título, links, erro, bytes, tempo) e contadores de itens descartados por motivo.
- CLI.

## Como executar

Pré-requisito: .NET 8 SDK.

```bash
dotnet build
dotnet run --project src/WebCrawler.Cli -- https://example.com --depth 2 --max-pages 50 --concurrency 4 --delay-ms 200
```

Opções: `--depth n`, `--max-pages n`, `--concurrency n`, `--delay-ms n`, `--any-host` (seguir links para outros hosts), `--no-robots`. Use apenas em sites que você tem permissão para rastrear. Para testar localmente, sem internet:

```bash
mkdir /tmp/site && echo '<title>Home</title><a href="a.html">a</a>' > /tmp/site/index.html && echo '<title>A</title>' > /tmp/site/a.html
python3 -m http.server 8765 --directory /tmp/site &
dotnet run --project src/WebCrawler.Cli -- http://127.0.0.1:8765/
```

Como biblioteca:

```csharp
using var http = new HttpClient();
var crawler = new Crawler(http, new CrawlerOptions { MaxDepth = 2, MaxConcurrency = 8, OnPage = p => Console.WriteLine(p.Url) });
CrawlReport report = await crawler.CrawlAsync(new Uri("https://example.com"));
```

## Como testar

```bash
dotnet test
```

Os 40 testes não acessam a internet. A maior parte usa um `FakeSite` (um `HttpMessageHandler` em memória, com latência configurável e medição de concorrência de pico). Dois testes sobem um `HttpListener` em `127.0.0.1` para validar o fluxo de ponta a ponta com redirecionamento 301, robots.txt e falha de conexão. Cobertura: parser de robots.txt, extração de links e título, normalização, grafos com ciclos, limites, concorrência (pico entre 2 e 5 com limite 5), erros HTTP/rede, timeouts, cancelamento, atraso por host e callback que lança exceção.

## Decisões de projeto

- **Canal + N workers.** O canal sem limite guarda a fronteira; N tarefas consomem e o limite de concorrência vem do número de workers. A memória da fronteira é limitada na prática por `MaxPages`.
- **Término correto.** `pending` é incrementado antes de enfileirar e decrementado só depois que o worker terminou de enfileirar os filhos; ao chegar a zero, o canal é completado. Assim nenhum worker encerra enquanto outro ainda pode produzir trabalho.
- **Dedup antes de enfileirar.** `ConcurrentDictionary.TryAdd` é a única barreira, de modo que cada URL é visitada uma vez em O(1). Para redirecionamentos, a URL final também é registrada; se outro item já a possui, o resultado é descartado.
- **robots.txt com `Lazy<Task>`.** Garante um único download por host mesmo com workers concorrentes.
- **Política conservadora para 5xx/erro de rede no robots.txt** (bloquear), como recomenda o RFC 9309; 4xx significa "sem restrições".
- **Cortesia por reserva de horário.** `PerHostDelay` é implementado reservando o próximo *slot* do host sob um lock curto e dormindo fora dele, o que mantém o espaçamento correto com workers concorrentes.
- **Parser de HTML minimalista.** Regex é suficiente para achar `<a href>` (e evita dependências), mas não entende `<base href>`, JavaScript ou HTML malformado complexo.

## Estrutura de pastas

```
WebCrawler/
├── WebCrawler.sln
├── src/
│   ├── WebCrawler/            # Crawler, RobotsTxt, HtmlParsing, UrlNormalizer, tipos
│   └── WebCrawler.Cli/        # CLI
└── tests/
    └── WebCrawler.Tests/      # FakeSite, LocalServer (HttpListener) e testes xUnit
```

## Limitações conhecidas

- Não executa JavaScript, então não rastreia aplicações de página única.
- Sem suporte a sitemaps, `<base href>`, `<meta robots>` ou `X-Robots-Tag`.
- Estado em memória, sem retomada após interrupção e sem fronteira persistente ou distribuída.
- Redirecionamentos cujo destino já foi enfileirado por outro caminho não geram um segundo resultado (a página é reportada uma vez, pela URL que a enfileirou primeiro).
- Normalização simples: não remove parâmetros de rastreamento nem reordena a query string.
- O atraso por host usa o relógio real (`DateTime.UtcNow`).

## Licença

MIT. Veja o arquivo [LICENSE](LICENSE).
