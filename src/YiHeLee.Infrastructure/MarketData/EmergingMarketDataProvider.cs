using YiHeLee.Application.Abstractions;
using YiHeLee.Application.Exceptions;
using YiHeLee.Domain;

namespace YiHeLee.Infrastructure.MarketData;

/// <summary>
/// 呼叫證券櫃檯買賣中心（TPEx）官方 OpenAPI「興櫃股票當日行情表」端點（tpex_esb_latest_statistics）。
/// 只負責 HTTP 請求與轉成 DTO；本端點沒有日期參數，targetDate 是否等於來源資料日期一律由
/// <see cref="Application.Abstractions.IMarketPriceService"/> 驗證，本 Provider 不得自行判斷成功與否。
/// </summary>
public sealed class EmergingMarketDataProvider : IEmergingMarketDataProvider
{
    private readonly HttpClient _httpClient;
    private readonly IAppLogger _logger;
    private readonly IClock _clock;
    private readonly Dictionary<(string StockCode, string RocMonth), EsbHistoricalDailyCloseParseResult> _historicalMonthCache = [];
    private readonly object _historicalMonthCacheLock = new();

    public EmergingMarketDataProvider(HttpClient httpClient, IAppLogger logger, IClock clock)
    {
        _httpClient = httpClient;
        _logger = logger;
        _clock = clock;
    }

    public string SourceProviderName => "TPEx興櫃";

    public async Task<OfficialPriceFetchResult> FetchDailyCloseAsync(
        DateOnly requestedDate,
        OfficialMarketDataSettings settings,
        CancellationToken cancellationToken)
    {
        var url = settings.EmergingDailyCloseUrl;
        var startedAt = _clock.GetTaipeiNow();
        Exception? lastError = null;
        var maxAttempts = Math.Max(1, settings.HttpShortRetryCount);

        for (var attempt = 1; attempt <= maxAttempts; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                var parsed = await FetchOnceAsync(url, settings, cancellationToken).ConfigureAwait(false);
                return new OfficialPriceFetchResult(
                    MarketType.Emerging,
                    requestedDate,
                    parsed.SourceDataDate,
                    parsed.Quotes,
                    parsed.IsExplicitNoData,
                    SourceProviderName,
                    url,
                    startedAt,
                    _clock.GetTaipeiNow());
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex) when (attempt < maxAttempts)
            {
                lastError = ex;
                _logger.Warning($"TPEx 興櫃官方當日行情第 {attempt} 次擷取失敗：{ex.Message}，將短暫重試。");
                await Task.Delay(TimeSpan.FromSeconds(Math.Max(1, settings.HttpShortRetryDelaySeconds)), cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                lastError = ex;
            }
        }

        throw new RetryableJobException($"TPEx 興櫃官方當日行情連續 {maxAttempts} 次擷取失敗：{lastError?.Message}", lastError);
    }

    public async Task<OfficialPriceFetchResult> FetchHistoricalDailyCloseAsync(
        DateOnly requestedDate,
        IReadOnlyCollection<string> stockCodes,
        OfficialMarketDataSettings settings,
        CancellationToken cancellationToken)
    {
        var startedAt = _clock.GetTaipeiNow();
        var codes = stockCodes
            .Select(MarketDataParsingHelpers.Normalize)
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        if (codes.Length == 0)
        {
            return new OfficialPriceFetchResult(
                MarketType.Emerging,
                requestedDate,
                null,
                [],
                true,
                SourceProviderName,
                settings.EmergingHistoricalUrlTemplate,
                startedAt,
                _clock.GetTaipeiNow());
        }

        Exception? lastError = null;
        var maxAttempts = Math.Max(1, settings.HttpShortRetryCount);
        for (var attempt = 1; attempt <= maxAttempts; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                var quotes = new List<OfficialPriceQuote>();
                var sourceUrls = new List<string>(codes.Length);
                foreach (var code in codes)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var (parsed, sourceUrl) = await FetchHistoricalMonthAsync(code, requestedDate, settings, cancellationToken).ConfigureAwait(false);
                    sourceUrls.Add(sourceUrl);
                    var row = parsed.Quotes.FirstOrDefault(x => x.TradeDate == requestedDate);
                    if (row is not null)
                    {
                        quotes.Add(row.Quote);
                    }
                }

                return new OfficialPriceFetchResult(
                    MarketType.Emerging,
                    requestedDate,
                    quotes.Count == 0 ? null : requestedDate,
                    quotes,
                    quotes.Count == 0,
                    SourceProviderName,
                    string.Join(";", sourceUrls.Distinct(StringComparer.OrdinalIgnoreCase)),
                    startedAt,
                    _clock.GetTaipeiNow());
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex) when (attempt < maxAttempts)
            {
                lastError = ex;
                _logger.Warning($"TPEx 興櫃歷史行情第 {attempt} 次擷取失敗：{ex.Message}，稍後重試。");
                await Task.Delay(TimeSpan.FromSeconds(Math.Max(1, settings.HttpShortRetryDelaySeconds)), cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                lastError = ex;
            }
        }

        throw new RetryableJobException($"TPEx 興櫃歷史行情擷取失敗，已重試 {maxAttempts} 次：{lastError?.Message}", lastError);
    }

    private async Task<EsbDailyCloseParseResult> FetchOnceAsync(
        string url,
        OfficialMarketDataSettings settings,
        CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.UserAgent.ParseAdd("Yi-He-Lee/1.0 (+Windows desktop official price collector)");

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(TimeSpan.FromSeconds(Math.Max(5, settings.HttpTimeoutSeconds)));

        using var response = await _httpClient.SendAsync(request, timeoutCts.Token).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            throw new RetryableJobException($"TPEx 興櫃 HTTP 回應失敗：{(int)response.StatusCode} {response.ReasonPhrase}");
        }

        var body = await response.Content.ReadAsStringAsync(timeoutCts.Token).ConfigureAwait(false);
        return EsbDailyCloseParser.Parse(body);
    }

    private async Task<(EsbHistoricalDailyCloseParseResult Parsed, string SourceUrl)> FetchHistoricalMonthAsync(
        string stockCode,
        DateOnly requestedDate,
        OfficialMarketDataSettings settings,
        CancellationToken cancellationToken)
    {
        var rocMonth = RocDateConverter.ToRocMonthSlash(requestedDate);
        var cacheKey = (stockCode, rocMonth);
        lock (_historicalMonthCacheLock)
        {
            if (_historicalMonthCache.TryGetValue(cacheKey, out var cached))
            {
                return (cached, BuildHistoricalUrl(stockCode, rocMonth, settings));
            }
        }

        var url = BuildHistoricalUrl(stockCode, rocMonth, settings);
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.UserAgent.ParseAdd("Yi-He-Lee/1.0 (+Windows desktop official price collector)");

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(TimeSpan.FromSeconds(Math.Max(5, settings.HttpTimeoutSeconds)));

        using var response = await _httpClient.SendAsync(request, timeoutCts.Token).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            throw new RetryableJobException($"TPEx 興櫃歷史行情 HTTP 回應失敗：{(int)response.StatusCode} {response.ReasonPhrase}");
        }

        var body = await response.Content.ReadAsStringAsync(timeoutCts.Token).ConfigureAwait(false);
        var parsed = EsbHistoricalDailyCloseParser.Parse(body, stockCode);
        lock (_historicalMonthCacheLock)
        {
            _historicalMonthCache[cacheKey] = parsed;
        }

        return (parsed, url);
    }

    private static string BuildHistoricalUrl(string stockCode, string rocMonth, OfficialMarketDataSettings settings)
        => string.Format(settings.EmergingHistoricalUrlTemplate, rocMonth, Uri.EscapeDataString(stockCode));
}
