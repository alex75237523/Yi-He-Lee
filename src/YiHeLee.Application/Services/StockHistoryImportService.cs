using YiHeLee.Application.Abstractions;
using YiHeLee.Domain;

namespace YiHeLee.Application.Services;

/// <summary>
/// 歷史收盤價並行回補協調服務。工作單位固定為「市場＋交易日期」：
/// 例如回補 5 個有效交易日、市場範圍為全部時，約產生 5×2＝10 個工作，而不是逐檔股票各發送一次請求。
/// 候選交易日一律排除週六、週日；國定假日／臨時休市等仍會實際送出請求，
/// 但官方來源會誠實回報休市或資料日期不符，由 <see cref="IMarketPriceService"/> 記為 Holiday，
/// 不計入有效交易日、也不會被視為失敗。若使用者需要涵蓋國定假日造成的落差，可提高設定的交易日數。
/// 抓取（HTTP，允許並行）與資料庫寫入（每個工作各自完整下載／解析／驗證後才進入單一交易 Upsert）
/// 皆重用既有 <see cref="IMarketPriceService"/>／Repository 既有安全機制，本服務只負責工作清單建立、
/// 有限並行排程、重試與進度記錄。
/// </summary>
public sealed class StockHistoryImportService : IStockHistoryImportService
{
    private readonly IMarketPriceService _marketPriceService;
    private readonly IStockPriceImportRepository _importRepository;
    private readonly IClock _clock;
    private readonly IAppLogger _logger;

    public StockHistoryImportService(
        IMarketPriceService marketPriceService,
        IStockPriceImportRepository importRepository,
        IClock clock,
        IAppLogger logger)
    {
        _marketPriceService = marketPriceService;
        _importRepository = importRepository;
        _clock = clock;
        _logger = logger;
    }

    public async Task<long> CreateJobAsync(
        StockHistoryImportRequest request,
        StockHistoryImportOptions options,
        CancellationToken cancellationToken)
    {
        IReadOnlyList<DateOnly> candidateDates;
        int requestedTradingDaysForRecord;
        DateOnly targetDateForRecord;

        if (request.StartDate is { } startDate && request.EndDate is { } endDate)
        {
            var rangeStart = startDate <= endDate ? startDate : endDate;
            var rangeEnd = startDate <= endDate ? endDate : startDate;
            candidateDates = BuildWeekdayRange(rangeStart, rangeEnd);
            requestedTradingDaysForRecord = candidateDates.Count;
            targetDateForRecord = rangeEnd;
        }
        else
        {
            var tradingDays = options.ClampTradingDays(request.TradingDays);
            var baseDate = _clock.GetTaipeiToday().AddDays(-1);
            candidateDates = BuildCandidateWeekdays(baseDate, tradingDays);
            requestedTradingDaysForRecord = tradingDays;
            targetDateForRecord = baseDate;
        }

        var markets = ResolveMarkets(request.Scope);

        var tasks = candidateDates
            .SelectMany(date => markets.Select(market => new StockPriceImportTaskDescriptor(market, date)))
            .ToList();

        var now = _clock.GetTaipeiNow();
        var creation = await _importRepository.CreateJobAsync(
            OfficialPriceJobType.HistoricalBackfill,
            requestedTradingDaysForRecord,
            targetDateForRecord,
            "Asia/Taipei",
            tasks,
            now,
            cancellationToken).ConfigureAwait(false);

        _logger.Info($"歷史收盤價回補批次 {creation.JobId} 已建立：市場範圍={request.Scope}，要求交易日數={requestedTradingDaysForRecord}，工作數={tasks.Count}。");
        return creation.JobId;
    }

    public async Task RunJobAsync(
        long jobId,
        OfficialMarketDataSettings marketDataSettings,
        StockHistoryImportOptions importOptions,
        CancellationToken cancellationToken)
    {
        await _importRepository.MarkJobRunningAsync(jobId, _clock.GetTaipeiNow(), cancellationToken).ConfigureAwait(false);

        var taskProgress = await _importRepository.GetTaskProgressAsync(jobId, cancellationToken).ConfigureAwait(false);
        var pendingTasks = taskProgress.Where(x => x.Status == StockPriceImportTaskStatus.Queued).ToList();

        // 抓取（HTTP）允許並行；每個工作各自呼叫既有 IMarketPriceService 完整下載／解析／驗證／Upsert，
        // 每個工作各自使用獨立資料庫連線與交易（既有 Repository 慣例：Cache=Shared＋busy_timeout，
        // 不會發生多執行緒共用同一個 DbConnection 寫入的情形）。
        var mergedSettings = BuildMergedSettings(marketDataSettings, importOptions);
        var parallelOptions = new ParallelOptions
        {
            MaxDegreeOfParallelism = importOptions.ClampedMaxConcurrency(),
            CancellationToken = cancellationToken
        };

        var cancelled = false;
        try
        {
            await Parallel.ForEachAsync(pendingTasks, parallelOptions, async (task, taskCancellationToken) =>
            {
                await ExecuteTaskAsync(task, mergedSettings, importOptions, taskCancellationToken).ConfigureAwait(false);
            }).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            cancelled = true;
        }

        if (cancelled)
        {
            await _importRepository.CancelJobAsync(jobId, _clock.GetTaipeiNow(), CancellationToken.None).ConfigureAwait(false);
            _logger.Info($"歷史收盤價回補批次 {jobId} 已取消。");
            return;
        }

        await _importRepository.FinalizeJobAsync(jobId, _clock.GetTaipeiNow(), CancellationToken.None).ConfigureAwait(false);
        _logger.Info($"歷史收盤價回補批次 {jobId} 執行完畢。");
    }

    private async Task ExecuteTaskAsync(
        StockPriceImportTaskProgress task,
        OfficialMarketDataSettings mergedSettings,
        StockHistoryImportOptions importOptions,
        CancellationToken cancellationToken)
    {
        await _importRepository.StartTaskAsync(task.TaskId, _clock.GetTaipeiNow(), cancellationToken).ConfigureAwait(false);

        var maxAttempts = 1 + Math.Max(0, importOptions.MaxRetryCount);
        Exception? lastError = null;
        OfficialPriceBatchSummary? lastSummary = null;

        for (var attempt = 1; attempt <= maxAttempts; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                lastSummary = await _marketPriceService.FetchAndSaveSingleAsync(
                    OfficialPriceJobType.HistoricalBackfill,
                    task.RequestedDate,
                    task.MarketType,
                    mergedSettings,
                    cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                // IMarketPriceService 對 HTTP／解析／驗證失敗一律內部捕捉並回傳 Failed／NotPublished 摘要
                // （見下方判斷），理論上這裡只會攔到非預期例外（例如 Repository 層問題），仍依既有分類規則決定是否重試。
                lastError = ex;
                var isLastAttempt = attempt >= maxAttempts;
                var transient = TransientFailureClassifier.IsTransient(ex);
                if (!transient || isLastAttempt)
                {
                    _logger.Warning(
                        $"{DescribeTask(task)} 第 {attempt} 次擷取發生非預期例外{(transient ? "（已達重試上限）" : "（非暫時性錯誤，不再重試）")}：{ex.Message}");
                    lastSummary = null;
                    break;
                }

                await DelayBeforeRetryAsync(task, importOptions, attempt, ex.Message, cancellationToken).ConfigureAwait(false);
                continue;
            }

            // 重點：MarketPriceService 對 HTTP／解析／驗證失敗一律內部捕捉並回傳 Failed／NotPublished 摘要，
            // 不會拋出例外；是否重試必須依這裡回傳的狀態與錯誤訊息判斷，否則暫時性錯誤永遠不會被重試
            // （此為實機串接 TWSE／TPEx 官方端點時發現並修正的行為）。
            if (lastSummary.Status is OfficialPriceBatchStatus.Failed or OfficialPriceBatchStatus.PartialFailed or OfficialPriceBatchStatus.NotPublished)
            {
                var isLastAttempt = attempt >= maxAttempts;
                var transient = TransientFailureClassifier.IsTransientMessage(lastSummary.ErrorMessage);
                if (!transient || isLastAttempt)
                {
                    break;
                }

                await DelayBeforeRetryAsync(task, importOptions, attempt, lastSummary.ErrorMessage, cancellationToken).ConfigureAwait(false);
                continue;
            }

            // Succeeded／Holiday／InsufficientHistory 等視為本次工作的最終結果，無需再重試。
            await RecordResultAsync(task.TaskId, lastSummary, attempt - 1, cancellationToken).ConfigureAwait(false);
            return;
        }

        if (lastSummary is not null)
        {
            await RecordResultAsync(task.TaskId, lastSummary, maxAttempts - 1, cancellationToken).ConfigureAwait(false);
            return;
        }

        await _importRepository.CompleteTaskAsync(
            task.TaskId,
            StockPriceImportTaskStatus.Failed,
            actualTradeDate: null,
            sourceUrl: null,
            retryCount: maxAttempts - 1,
            totalRows: 0,
            insertedRows: 0,
            updatedRows: 0,
            skippedRows: 0,
            failedRows: 0,
            completedAt: _clock.GetTaipeiNow(),
            errorMessage: lastError?.Message ?? "未知錯誤，未能完成本工作。",
            CancellationToken.None).ConfigureAwait(false);
    }

    private async Task DelayBeforeRetryAsync(
        StockPriceImportTaskProgress task,
        StockHistoryImportOptions importOptions,
        int attempt,
        string? errorMessage,
        CancellationToken cancellationToken)
    {
        var delaySeconds = importOptions.RetryBaseDelaySeconds * Math.Pow(2, attempt - 1);
        _logger.Warning($"{DescribeTask(task)} 第 {attempt} 次擷取失敗，{delaySeconds:0} 秒後重試：{errorMessage}");
        await Task.Delay(TimeSpan.FromSeconds(delaySeconds), cancellationToken).ConfigureAwait(false);
    }

    private async Task RecordResultAsync(long taskId, OfficialPriceBatchSummary summary, int retryCount, CancellationToken cancellationToken)
    {
        var status = summary.Status switch
        {
            OfficialPriceBatchStatus.Succeeded => StockPriceImportTaskStatus.Succeeded,
            OfficialPriceBatchStatus.Holiday => StockPriceImportTaskStatus.Holiday,
            OfficialPriceBatchStatus.NotPublished => StockPriceImportTaskStatus.WaitingForSource,
            _ => StockPriceImportTaskStatus.Failed
        };

        await _importRepository.CompleteTaskAsync(
            taskId,
            status,
            summary.SourceDataDate,
            sourceUrl: null,
            retryCount,
            totalRows: summary.FetchedCount,
            insertedRows: summary.InsertedCount,
            updatedRows: summary.UpdatedCount,
            skippedRows: summary.SkippedCount,
            failedRows: summary.FailedCount,
            completedAt: summary.FetchEndAt ?? _clock.GetTaipeiNow(),
            errorMessage: summary.ErrorMessage,
            cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// 建立 N 個候選有效交易日（排除週六、週日），由 baseDate 往前推算。國定假日／臨時休市等
    /// 無法事先得知的休市日，仍會實際送出請求並誠實記錄為 Holiday，不計入有效交易日、也不算失敗；
    /// 若休市日落在視窗內導致實際有效交易日少於要求天數，使用者可提高交易日數設定重新回補更早的日期。
    /// </summary>
    internal static IReadOnlyList<DateOnly> BuildCandidateWeekdays(DateOnly baseDate, int tradingDays)
    {
        var dates = new List<DateOnly>(tradingDays);
        var cursor = baseDate;
        while (dates.Count < tradingDays)
        {
            if (cursor.DayOfWeek is not (DayOfWeek.Saturday or DayOfWeek.Sunday))
            {
                dates.Add(cursor);
            }

            cursor = cursor.AddDays(-1);
        }

        return dates;
    }

    /// <summary>
    /// 依使用者指定的日期區間（含首尾）建立候選有效交易日，排除週六、週日；
    /// 國定假日／臨時休市等仍會實際送出請求並誠實記錄為 Holiday，不計入有效交易日、也不算失敗。
    /// </summary>
    internal static IReadOnlyList<DateOnly> BuildWeekdayRange(DateOnly startDate, DateOnly endDate)
    {
        var dates = new List<DateOnly>();
        for (var cursor = startDate; cursor <= endDate; cursor = cursor.AddDays(1))
        {
            if (cursor.DayOfWeek is not (DayOfWeek.Saturday or DayOfWeek.Sunday))
            {
                dates.Add(cursor);
            }
        }

        return dates;
    }

    private static IReadOnlyList<MarketType> ResolveMarkets(MarketScope scope) => scope switch
    {
        MarketScope.Listed => [MarketType.Listed],
        MarketScope.Otc => [MarketType.Otc],
        _ => [MarketType.Listed, MarketType.Otc]
    };

    private static OfficialMarketDataSettings BuildMergedSettings(OfficialMarketDataSettings baseSettings, StockHistoryImportOptions importOptions) => new()
    {
        TwseDailyCloseUrlTemplate = baseSettings.TwseDailyCloseUrlTemplate,
        TpexDailyCloseUrlTemplate = baseSettings.TpexDailyCloseUrlTemplate,
        EmergingDailyCloseUrl = baseSettings.EmergingDailyCloseUrl,
        EmergingHistoricalUrlTemplate = baseSettings.EmergingHistoricalUrlTemplate,
        HttpTimeoutSeconds = importOptions.RequestTimeoutSeconds,
        // 內層 Provider 重試次數固定為1（不重試）：本服務在外層依 MaxRetryCount 做指數退避重試，
        // 避免內外兩層重試機制重疊造成延遲不可預期。
        HttpShortRetryCount = 1,
        HttpShortRetryDelaySeconds = baseSettings.HttpShortRetryDelaySeconds,
        RequiredTradingDaysForMa120 = baseSettings.RequiredTradingDaysForMa120,
        MaxBackfillLookbackCalendarDays = baseSettings.MaxBackfillLookbackCalendarDays,
        BackfillThrottleMillisecondsBetweenRequests = baseSettings.BackfillThrottleMillisecondsBetweenRequests
    };

    private static string DescribeTask(StockPriceImportTaskProgress task)
        => $"{task.MarketType switch
        {
            MarketType.Listed => "上市",
            MarketType.Otc => "上櫃",
            MarketType.Emerging => "興櫃",
            _ => task.MarketType.ToString()
        }}／{task.RequestedDate:yyyy-MM-dd}";
}
