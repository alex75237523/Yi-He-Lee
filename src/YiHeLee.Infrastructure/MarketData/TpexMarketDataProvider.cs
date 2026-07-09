using YiHeLee.Application.Abstractions;
using YiHeLee.Application.Exceptions;
using YiHeLee.Domain;

namespace YiHeLee.Infrastructure.MarketData;

/// <summary>
/// 呼叫證券櫃檯買賣中心（TPEx）官方「上櫃股票行情」端點（stk_quote_result.php，民國年日期）。
/// 本端點對非交易日／未來日期會靜默改回傳最近一個交易日資料，Provider 誠實回報來源日期，
/// 由 Service 層嚴格比對 targetDate，不得只憑 HTTP 200 視為成功。
/// </summary>
public sealed class TpexMarketDataProvider : ITpexMarketDataProvider
{
    private readonly HttpClient _httpClient;
    private readonly IAppLogger _logger;
    private readonly IClock _clock;

    public TpexMarketDataProvider(HttpClient httpClient, IAppLogger logger, IClock clock)
    {
        _httpClient = httpClient;
        _logger = logger;
        _clock = clock;
    }

    public string SourceProviderName => "TPEx";

    public async Task<OfficialPriceFetchResult> FetchDailyCloseAsync(
        DateOnly requestedDate,
        OfficialMarketDataSettings settings,
        CancellationToken cancellationToken)
    {
        var url = string.Format(settings.TpexDailyCloseUrlTemplate, RocDateConverter.ToRocSlash(requestedDate));
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
                    MarketType.Otc,
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
                _logger.Warning($"TPEx 官方每日收盤行情第 {attempt} 次擷取失敗：{ex.Message}，將短暫重試。");
                await Task.Delay(TimeSpan.FromSeconds(Math.Max(1, settings.HttpShortRetryDelaySeconds)), cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                lastError = ex;
            }
        }

        throw new RetryableJobException($"TPEx 官方每日收盤行情連續 {maxAttempts} 次擷取失敗：{lastError?.Message}", lastError);
    }

    private async Task<TpexDailyCloseParseResult> FetchOnceAsync(
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
            throw new RetryableJobException($"TPEx HTTP 回應失敗：{(int)response.StatusCode} {response.ReasonPhrase}");
        }

        var body = await response.Content.ReadAsStringAsync(timeoutCts.Token).ConfigureAwait(false);
        return TpexDailyCloseParser.Parse(body);
    }
}
