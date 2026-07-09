using System.Globalization;
using System.Text.Json;
using Microsoft.Playwright;
using YiHeLee.Application.Abstractions;
using YiHeLee.Application.Exceptions;
using YiHeLee.Domain;
using IClock = YiHeLee.Application.Abstractions.IClock;

namespace YiHeLee.Infrastructure.Crawlers;

/// <summary>
/// 鉅亨網股價多頭／空頭排列專用爬蟲。
/// 每一個未來新增的網站應建立自己的 ISourceCrawler，不把不同頁面解析規則混在一起。
/// </summary>
public sealed class CnyesTechnicalAlignmentCrawler : ISourceCrawler, IAsyncDisposable
{
    private readonly IClock _clock;
    private readonly IAppLogger _logger;
    private readonly SemaphoreSlim _browserLock = new(1, 1);
    private readonly CnyesTechnicalAlignmentParser _parser = new();
    private IPlaywright? _playwright;
    private IBrowser? _browser;

    public CnyesTechnicalAlignmentCrawler(IClock clock, IAppLogger logger)
    {
        _clock = clock;
        _logger = logger;
    }

    public string ProviderKey => "CnyesTechnicalAlignment";

    public async Task<CrawlBatch> CrawlAsync(
        SourceDefinition source,
        MarketType marketType,
        DateOnly targetDate,
        AppSettings settings,
        CancellationToken cancellationToken)
    {
        Exception? lastError = null;
        var maxAttempts = Math.Max(1, settings.CrawlerShortRetryCount);

        for (var attempt = 1; attempt <= maxAttempts; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                return await CrawlOnceAsync(source, marketType, targetDate, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex) when (attempt < maxAttempts)
            {
                lastError = ex;
                _logger.Warning($"{source.DisplayName}／{ToMarketText(marketType)} 第 {attempt} 次爬取失敗：{ex.Message}，將短暫重試。");
                await Task.Delay(TimeSpan.FromSeconds(Math.Max(1, settings.CrawlerShortRetryDelaySeconds)), cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                lastError = ex;
            }
        }

        throw new RetryableJobException(
            $"{source.DisplayName}／{ToMarketText(marketType)} 連續 {maxAttempts} 次爬取失敗：{lastError?.Message}",
            lastError);
    }

    private async Task<CrawlBatch> CrawlOnceAsync(
        SourceDefinition source,
        MarketType marketType,
        DateOnly targetDate,
        CancellationToken cancellationToken)
    {
        var startedAt = _clock.GetTaipeiNow();
        var browser = await GetBrowserAsync(cancellationToken).ConfigureAwait(false);
        await using var context = await browser.NewContextAsync(new BrowserNewContextOptions
        {
            Locale = "zh-TW",
            UserAgent = "Yi-He-Lee/1.0 (+Windows desktop technical indicator collector)",
            ViewportSize = new ViewportSize { Width = 1440, Height = 1000 }
        }).ConfigureAwait(false);
        var page = await context.NewPageAsync().ConfigureAwait(false);
        page.SetDefaultTimeout(30_000);
        page.SetDefaultNavigationTimeout(45_000);

        var response = await page.GotoAsync(source.Url.ToString(), new PageGotoOptions
        {
            WaitUntil = WaitUntilState.DOMContentLoaded,
            Timeout = 45_000
        }).ConfigureAwait(false);

        if (response is null || !response.Ok)
        {
            throw new RetryableJobException($"HTTP 回應失敗：{response?.Status} {response?.StatusText}");
        }

        await WaitForPageAsync(page, cancellationToken).ConfigureAwait(false);
        await ClickExactLinkIfPresentAsync(page, ToMarketText(marketType), cancellationToken).ConfigureAwait(false);
        await ClickExactLinkIfPresentAsync(page, targetDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), cancellationToken).ConfigureAwait(false);

        var snapshotJson = await page.EvaluateAsync<string>(DomSnapshotScript).ConfigureAwait(false);
        var snapshot = JsonSerializer.Deserialize<DomSnapshot>(snapshotJson, JsonOptions)
                       ?? throw new RetryableJobException("頁面 DOM 解析結果為空。");

        if (!snapshot.FoundTable)
        {
            throw new RetryableJobException("找不到包含代碼、名稱、收盤價及 5／20／60／120 日均價的表格，網站結構可能已變更。");
        }

        var expectedTitle = source.IndicatorType == IndicatorType.BullishAlignment ? "股價多頭排列" : "股價空頭排列";
        if (!snapshot.ContextText.Contains(expectedTitle, StringComparison.Ordinal))
        {
            throw new RetryableJobException($"表格附近未找到「{expectedTitle}」，資料類型驗證失敗。");
        }

        var marketText = ToMarketText(marketType);
        if (!snapshot.ContextText.Contains(marketText, StringComparison.Ordinal))
        {
            throw new RetryableJobException($"表格附近未找到「{marketText}」，市場別驗證失敗。");
        }

        if (snapshot.HasPagination)
        {
            // 目前頁面應一次顯示完整清單；若未來出現分頁，不可只保存第一頁。
            throw new RetryableJobException("偵測到表格分頁控制，但目前 Provider 尚未完成分頁遍歷，為避免部分資料已拒絕寫入。");
        }

        var pageDate = _parser.ExtractDisplayedDate(snapshot.ContextText);
        var items = _parser.ParseRows(snapshot.Rows, source, marketType, pageDate, startedAt, _clock.GetTaipeiNow());
        var explicitNoData = snapshot.ExplicitNoData;

        if (items.Count == 0 && !explicitNoData)
        {
            throw new RetryableJobException("表格存在但沒有可解析資料列，且頁面沒有明確顯示查無資料。");
        }

        return new CrawlBatch(
            source,
            marketType,
            targetDate,
            pageDate,
            items,
            startedAt,
            _clock.GetTaipeiNow(),
            explicitNoData,
            snapshot.ContextText.Length > 1000 ? snapshot.ContextText[..1000] : snapshot.ContextText);
    }

    private async Task<IBrowser> GetBrowserAsync(CancellationToken cancellationToken)
    {
        if (_browser is not null && _browser.IsConnected)
        {
            return _browser;
        }

        await _browserLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_browser is not null && _browser.IsConnected)
            {
                return _browser;
            }

            _playwright ??= await Playwright.CreateAsync().ConfigureAwait(false);
            try
            {
                // 優先使用 Windows 內建 Microsoft Edge，降低額外瀏覽器安裝需求。
                _browser = await _playwright.Chromium.LaunchAsync(new BrowserTypeLaunchOptions
                {
                    Headless = true,
                    Channel = "msedge"
                }).ConfigureAwait(false);
            }
            catch (PlaywrightException edgeException)
            {
                _logger.Warning($"無法啟動 Microsoft Edge，改嘗試 Playwright Chromium：{edgeException.Message}");
                try
                {
                    _browser = await _playwright.Chromium.LaunchAsync(new BrowserTypeLaunchOptions
                    {
                        Headless = true
                    }).ConfigureAwait(false);
                }
                catch (PlaywrightException chromiumException)
                {
                    throw new RetryableJobException(
                        "無法啟動 Edge 或 Playwright Chromium。請執行 scripts\\install-playwright-browser.ps1，或確認電腦已安裝 Microsoft Edge。",
                        chromiumException);
                }
            }

            return _browser;
        }
        finally
        {
            _browserLock.Release();
        }
    }

    private static async Task ClickExactLinkIfPresentAsync(IPage page, string text, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var escaped = text.Replace("'", "&apos;", StringComparison.Ordinal);
        var locator = page.Locator($"xpath=//a[normalize-space(.)='{escaped}']");
        if (await locator.CountAsync().ConfigureAwait(false) == 0)
        {
            return;
        }

        await locator.First.ClickAsync(new LocatorClickOptions { Timeout = 15_000 }).ConfigureAwait(false);
        await WaitForPageAsync(page, cancellationToken).ConfigureAwait(false);
    }

    private static async Task WaitForPageAsync(IPage page, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            await page.WaitForLoadStateAsync(LoadState.DOMContentLoaded, new PageWaitForLoadStateOptions { Timeout = 15_000 }).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            // 廣告或追蹤資源可能持續載入；只要主要 DOM 已存在即可繼續。
        }

        await page.WaitForTimeoutAsync(1200).ConfigureAwait(false);
    }

    private static string ToMarketText(MarketType marketType) => marketType == MarketType.Listed ? "集中市場" : "店頭市場";

    public async ValueTask DisposeAsync()
    {
        if (_browser is not null)
        {
            await _browser.DisposeAsync().ConfigureAwait(false);
        }

        _playwright?.Dispose();
        _browserLock.Dispose();
    }

    private sealed class DomSnapshot
    {
        public bool FoundTable { get; set; }
        public string ContextText { get; set; } = string.Empty;
        public List<string[]> Rows { get; set; } = [];
        public bool ExplicitNoData { get; set; }
        public bool HasPagination { get; set; }
    }

    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };

    private const string DomSnapshotScript = """
        (() => {
          const normalize = value => (value || '').replace(/\s+/g, ' ').trim();
          const required = ['代碼', '名稱', '收盤價', '5日均價', '20日均價', '60日均價', '120日均價'];
          const tables = Array.from(document.querySelectorAll('table'));
          const table = tables.find(candidate => {
            const text = normalize(candidate.innerText).replace(/ /g, '');
            return required.every(header => text.includes(header));
          });

          if (!table) {
            return JSON.stringify({ foundTable: false, contextText: normalize(document.body.innerText).slice(-2000), rows: [], explicitNoData: false, hasPagination: false });
          }

          const bodyText = normalize(document.body.innerText);
          const tableText = normalize(table.innerText);
          const index = bodyText.indexOf(tableText);
          const before = index >= 0 ? bodyText.substring(Math.max(0, index - 1200), index) : bodyText.substring(0, 1200);
          const contextText = normalize(before + ' ' + tableText.substring(0, 500));
          const rows = Array.from(table.querySelectorAll('tr')).map(row =>
            Array.from(row.querySelectorAll('th,td')).map(cell => normalize(cell.innerText))
          );
          const container = table.parentElement || table;
          const containerText = normalize(container.innerText);
          const explicitNoData = /查無資料|無符合資料|目前沒有資料|無資料/.test(containerText);
          const hasPagination = Array.from(container.querySelectorAll('a,button')).some(element => {
            const text = normalize(element.innerText || element.getAttribute('aria-label'));
            const disabled = element.hasAttribute('disabled') || element.getAttribute('aria-disabled') === 'true' || element.classList.contains('disabled');
            return !disabled && /^(下一頁|下頁|Next|›|»)$/.test(text);
          });
          return JSON.stringify({ foundTable: true, contextText, rows, explicitNoData, hasPagination });
        })()
        """;
}
