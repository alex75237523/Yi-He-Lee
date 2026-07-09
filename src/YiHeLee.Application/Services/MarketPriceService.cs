using YiHeLee.Application.Abstractions;
using YiHeLee.Domain;

namespace YiHeLee.Application.Services;

/// <summary>
/// 官方每日收盤價協調服務。負責：
/// 台北日期與來源資料日期比對、休市／尚未公布判斷、冪等 Upsert、批次狀態記錄。
/// TWSE 對非交易日會明確回報「查無資料」，做為休市權威判斷依據；
/// TPEx 對非交易日會靜默回傳最近一個交易日資料，因此只有在同一天 TWSE 已確認休市時，
/// 才會把 TPEx 的日期不符情形也記錄為 Holiday，否則一律視為 NotPublished 並等待重試，
/// 絕不允許把前一交易日資料當成目標日期寫入正式資料表。
/// </summary>
public sealed class MarketPriceService : IMarketPriceService
{
    private readonly ITwseMarketDataProvider _twseProvider;
    private readonly ITpexMarketDataProvider _tpexProvider;
    private readonly IMarketDataRepository _repository;
    private readonly IClock _clock;
    private readonly IAppLogger _logger;

    public MarketPriceService(
        ITwseMarketDataProvider twseProvider,
        ITpexMarketDataProvider tpexProvider,
        IMarketDataRepository repository,
        IClock clock,
        IAppLogger logger)
    {
        _twseProvider = twseProvider;
        _tpexProvider = tpexProvider;
        _repository = repository;
        _clock = clock;
        _logger = logger;
    }

    public async Task<IReadOnlyList<OfficialPriceBatchSummary>> FetchAndSaveDailyPricesAsync(
        DateOnly targetDate,
        OfficialMarketDataSettings settings,
        CancellationToken cancellationToken)
    {
        var summaries = new List<OfficialPriceBatchSummary>();

        if (targetDate.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday)
        {
            // 週末非交易日，無需呼叫官方來源即可確定為休市，避免不必要的網路請求。
            summaries.Add(await RecordHolidayWithoutFetchAsync(OfficialPriceJobType.DailyMarketData, targetDate, _twseProvider.SourceProviderName, MarketType.Listed, cancellationToken).ConfigureAwait(false));
            summaries.Add(await RecordHolidayWithoutFetchAsync(OfficialPriceJobType.DailyMarketData, targetDate, _tpexProvider.SourceProviderName, MarketType.Otc, cancellationToken).ConfigureAwait(false));
            return summaries;
        }

        var twseSummary = await FetchAndSaveOneAsync(OfficialPriceJobType.DailyMarketData, targetDate, MarketType.Listed, settings, cancellationToken).ConfigureAwait(false);
        summaries.Add(twseSummary);

        var twseIsHoliday = twseSummary.Status == OfficialPriceBatchStatus.Holiday;
        var tpexSummary = await FetchAndSaveOneAsync(
            OfficialPriceJobType.DailyMarketData, targetDate, MarketType.Otc, settings, cancellationToken,
            treatDateMismatchAsHoliday: twseIsHoliday).ConfigureAwait(false);
        summaries.Add(tpexSummary);

        return summaries;
    }

    public async Task<IReadOnlyList<OfficialPriceBatchSummary>> BackfillHistoryAsync(
        DateOnly targetDate,
        OfficialMarketDataSettings settings,
        CancellationToken cancellationToken)
    {
        var summaries = new List<OfficialPriceBatchSummary>();
        var requiredTradingDays = Math.Max(1, settings.RequiredTradingDaysForMa120);
        var maxLookbackCalendarDays = Math.Max(requiredTradingDays, settings.MaxBackfillLookbackCalendarDays);

        // 歷史回補只補建 targetDate「以前」的資料；targetDate 當日一律由每日正式排程負責，兩者不得混用。
        // 判斷「是否已足夠」一律以固定基準日（targetDate 前一日）為準，不可隨 cursor 逐日倒退而跟著縮小查詢範圍。
        var referenceDate = targetDate.AddDays(-1);
        var cursor = referenceDate;
        for (var walked = 0; walked < maxLookbackCalendarDays; walked++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var existingTradingDays = await _repository.GetDistinctTradeDateCountAsync(referenceDate, requiredTradingDays, cancellationToken).ConfigureAwait(false);
            if (existingTradingDays >= requiredTradingDays)
            {
                break;
            }

            if (cursor.DayOfWeek is not (DayOfWeek.Saturday or DayOfWeek.Sunday))
            {
                var twseSummary = await FetchAndSaveOneAsync(OfficialPriceJobType.HistoricalBackfill, cursor, MarketType.Listed, settings, cancellationToken).ConfigureAwait(false);
                summaries.Add(twseSummary);

                var isHoliday = twseSummary.Status == OfficialPriceBatchStatus.Holiday;
                var tpexSummary = await FetchAndSaveOneAsync(
                    OfficialPriceJobType.HistoricalBackfill, cursor, MarketType.Otc, settings, cancellationToken,
                    treatDateMismatchAsHoliday: isHoliday).ConfigureAwait(false);
                summaries.Add(tpexSummary);

                // 避免大量平行轟炸官方網站；逐日之間節流。
                await Task.Delay(TimeSpan.FromMilliseconds(Math.Max(0, settings.BackfillThrottleMillisecondsBetweenRequests)), cancellationToken).ConfigureAwait(false);
            }

            cursor = cursor.AddDays(-1);
        }

        return summaries;
    }

    private async Task<OfficialPriceBatchSummary> FetchAndSaveOneAsync(
        OfficialPriceJobType jobType,
        DateOnly targetDate,
        MarketType marketType,
        OfficialMarketDataSettings settings,
        CancellationToken cancellationToken,
        bool treatDateMismatchAsHoliday = false)
    {
        var providerName = marketType == MarketType.Listed ? _twseProvider.SourceProviderName : _tpexProvider.SourceProviderName;

        if (await _repository.HasSucceededBatchAsync(jobType, targetDate, providerName, cancellationToken).ConfigureAwait(false))
        {
            _logger.Info($"{providerName} {targetDate:yyyy-MM-dd}（{jobType}）官方收盤價本次已成功保存過，略過重複抓取。");
            return CreateSyntheticSummary(jobType, targetDate, providerName, marketType, OfficialPriceBatchStatus.Succeeded, "同一目標日期先前已成功寫入，本次略過重複抓取（冪等）。");
        }

        var startedAt = _clock.GetTaipeiNow();
        var batchId = await _repository.BeginPriceBatchAsync(jobType, targetDate, providerName, marketType, startedAt, cancellationToken).ConfigureAwait(false);

        OfficialPriceFetchResult fetchResult;
        try
        {
            fetchResult = marketType == MarketType.Listed
                ? await _twseProvider.FetchDailyCloseAsync(targetDate, settings, cancellationToken).ConfigureAwait(false)
                : await _tpexProvider.FetchDailyCloseAsync(targetDate, settings, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            var failSummary = new OfficialPriceBatchSummary(
                batchId, jobType, targetDate, providerName, marketType, null,
                startedAt, _clock.GetTaipeiNow(), 0, 0, 0, 0, 0, 0,
                OfficialPriceBatchStatus.Failed, ex.Message);
            await _repository.CompletePriceBatchAsync(failSummary, cancellationToken).ConfigureAwait(false);
            _logger.Error($"{providerName} {targetDate:yyyy-MM-dd} 官方每日收盤價擷取失敗。", ex);
            return failSummary;
        }

        if (fetchResult.IsHolidayOrNoData)
        {
            var holidaySummary = new OfficialPriceBatchSummary(
                batchId, jobType, targetDate, providerName, marketType, fetchResult.SourceDataDate,
                startedAt, _clock.GetTaipeiNow(), fetchResult.Quotes.Count, 0, 0, 0, 0, 0,
                OfficialPriceBatchStatus.Holiday, "官方來源明確回報查無資料（休市或非交易日）。");
            await _repository.CompletePriceBatchAsync(holidaySummary, cancellationToken).ConfigureAwait(false);
            _logger.Info($"{providerName} {targetDate:yyyy-MM-dd} 官方來源回報休市／無交易資料。");
            return holidaySummary;
        }

        // 核心規則：來源資料日期必須完全等於 targetDate，否則絕不可寫入正式資料，也不得回抓前一交易日。
        if (fetchResult.SourceDataDate is null || fetchResult.SourceDataDate.Value != targetDate)
        {
            var status = treatDateMismatchAsHoliday ? OfficialPriceBatchStatus.Holiday : OfficialPriceBatchStatus.NotPublished;
            var message = treatDateMismatchAsHoliday
                ? $"{providerName} 回傳資料日期為 {fetchResult.SourceDataDate:yyyy-MM-dd}，與同日 TWSE 已確認之休市狀態一致，記錄為休市，不寫入正式資料。"
                : $"{providerName} 回傳資料日期為「{fetchResult.SourceDataDate?.ToString("yyyy-MM-dd") ?? "無"}」，尚未等於目標日期 {targetDate:yyyy-MM-dd}，依規定不得寫入正式資料表，待重試機制稍後重試。";
            var mismatchSummary = new OfficialPriceBatchSummary(
                batchId, jobType, targetDate, providerName, marketType, fetchResult.SourceDataDate,
                startedAt, _clock.GetTaipeiNow(), fetchResult.Quotes.Count, 0, 0, fetchResult.Quotes.Count, 0, 0,
                status, message);
            await _repository.CompletePriceBatchAsync(mismatchSummary, cancellationToken).ConfigureAwait(false);
            _logger.Warning(message);
            return mismatchSummary;
        }

        if (fetchResult.Quotes.Count == 0)
        {
            var emptySummary = new OfficialPriceBatchSummary(
                batchId, jobType, targetDate, providerName, marketType, fetchResult.SourceDataDate,
                startedAt, _clock.GetTaipeiNow(), 0, 0, 0, 0, 0, 0,
                OfficialPriceBatchStatus.Failed, "來源資料日期正確，但沒有任何可解析的股票收盤價，視為解析失敗，不得標記為成功。");
            await _repository.CompletePriceBatchAsync(emptySummary, cancellationToken).ConfigureAwait(false);
            _logger.Warning(emptySummary.ErrorMessage!);
            return emptySummary;
        }

        var prices = fetchResult.Quotes
            .Select(quote => new OfficialStockPrice(
                quote.StockCode,
                quote.StockName,
                marketType,
                targetDate,
                quote.ClosePrice,
                providerName,
                fetchResult.SourceUrl,
                fetchResult.SourceDataDate.Value,
                batchId,
                fetchResult.FetchCompletedAt))
            .ToArray();

        var (inserted, updated) = await _repository.UpsertDailyPricesAsync(prices, cancellationToken).ConfigureAwait(false);

        var successSummary = new OfficialPriceBatchSummary(
            batchId, jobType, targetDate, providerName, marketType, fetchResult.SourceDataDate,
            startedAt, _clock.GetTaipeiNow(), prices.Length, inserted, updated, 0, 0, 0,
            OfficialPriceBatchStatus.Succeeded, null);
        await _repository.CompletePriceBatchAsync(successSummary, cancellationToken).ConfigureAwait(false);
        _logger.Info($"{providerName} {targetDate:yyyy-MM-dd}（{jobType}）官方收盤價寫入成功：新增 {inserted} 筆、更新 {updated} 筆。");
        return successSummary;
    }

    private async Task<OfficialPriceBatchSummary> RecordHolidayWithoutFetchAsync(
        OfficialPriceJobType jobType,
        DateOnly targetDate,
        string providerName,
        MarketType marketType,
        CancellationToken cancellationToken)
    {
        var startedAt = _clock.GetTaipeiNow();
        var batchId = await _repository.BeginPriceBatchAsync(jobType, targetDate, providerName, marketType, startedAt, cancellationToken).ConfigureAwait(false);
        var summary = new OfficialPriceBatchSummary(
            batchId, jobType, targetDate, providerName, marketType, null,
            startedAt, _clock.GetTaipeiNow(), 0, 0, 0, 0, 0, 0,
            OfficialPriceBatchStatus.Holiday, "週六、週日非交易日，未呼叫官方來源。");
        await _repository.CompletePriceBatchAsync(summary, cancellationToken).ConfigureAwait(false);
        return summary;
    }

    private static OfficialPriceBatchSummary CreateSyntheticSummary(
        OfficialPriceJobType jobType,
        DateOnly targetDate,
        string providerName,
        MarketType marketType,
        OfficialPriceBatchStatus status,
        string message)
        => new(
            $"(skip-{Guid.NewGuid():N})", jobType, targetDate, providerName, marketType, targetDate,
            DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, 0, 0, 0, 0, 0, 0, status, message);
}
