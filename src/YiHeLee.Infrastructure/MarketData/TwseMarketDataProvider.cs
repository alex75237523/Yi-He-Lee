using YiHeLee.Application.Abstractions;
using YiHeLee.Application.Exceptions;
using YiHeLee.Domain;

namespace YiHeLee.Infrastructure.MarketData;

/// <summary>
/// 呼叫臺灣證券交易所（TWSE）官方「每日收盤行情-大盤統計資訊」端點（MI_INDEX, type=ALLBUT0999）。
/// 只負責 HTTP 請求與轉成 DTO；targetDate 是否等於來源資料日期由 <see cref="Application.Abstractions.IMarketPriceService"/> 驗證。
/// </summary>
public sealed class TwseMarketDataProvider : ITwseMarketDataProvider
{
    private readonly HttpClient _httpClient;
    private readonly IAppLogger _logger;
    private readonly IClock _clock;

    public TwseMarketDataProvider(HttpClient httpClient, IAppLogger logger, IClock clock)
    {
        _httpClient = httpClient;
        _logger = logger;
        _clock = clock;
    }

    public string SourceProviderName => "TWSE";

    public async Task<OfficialPriceFetchResult> FetchDailyCloseAsync(
        DateOnly requestedDate,
        OfficialMarketDataSettings settings,
        CancellationToken cancellationToken)
    {
        var url = string.Format(settings.TwseDailyCloseUrlTemplate, RocDateConverter.ToWesternCompact(requestedDate));
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
                    MarketType.Listed,
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
                _logger.Warning($"TWSE 官方每日收盤行情第 {attempt} 次擷取失敗：{ex.Message}，將短暫重試。");
                await Task.Delay(TimeSpan.FromSeconds(Math.Max(1, settings.HttpShortRetryDelaySeconds)), cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                lastError = ex;
            }
        }

        throw new RetryableJobException($"TWSE 官方每日收盤行情連續 {maxAttempts} 次擷取失敗：{lastError?.Message}", lastError);
    }

    private async Task<TwseDailyCloseParseResult> FetchOnceAsync(
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
            throw new RetryableJobException($"TWSE HTTP 回應失敗：{(int)response.StatusCode} {response.ReasonPhrase}");
        }

        var body = await response.Content.ReadAsStringAsync(timeoutCts.Token).ConfigureAwait(false);
        return TwseDailyCloseParser.Parse(body);
    }
}
