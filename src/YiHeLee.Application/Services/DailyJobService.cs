using YiHeLee.Application.Abstractions;
using YiHeLee.Application.Exceptions;
using YiHeLee.Domain;

namespace YiHeLee.Application.Services;

public sealed class DailyJobService
{
    private readonly IClock _clock;
    private readonly ISettingsStore _settingsStore;
    private readonly ICrawlerRegistry _crawlerRegistry;
    private readonly IYiHeLeeRepository _repository;
    private readonly IMarketDataRepository _marketDataRepository;
    private readonly IExcelWorkbookService _excelWorkbookService;
    private readonly IUserInteraction _userInteraction;
    private readonly IAppLogger _logger;
    private readonly DailyMarketDataJob _dailyMarketDataJob;
    private readonly HistoricalBackfillJob _historicalBackfillJob;
    private readonly IMovingAverageService _movingAverageService;
    private readonly StrategyEvaluationService _strategyEvaluationService;
    private readonly SettingsValidationService _settingsValidationService;
    private readonly SemaphoreSlim _singleRunLock = new(1, 1);

    public DailyJobService(
        IClock clock,
        ISettingsStore settingsStore,
        ICrawlerRegistry crawlerRegistry,
        IYiHeLeeRepository repository,
        IMarketDataRepository marketDataRepository,
        IExcelWorkbookService excelWorkbookService,
        IUserInteraction userInteraction,
        IAppLogger logger,
        DailyMarketDataJob dailyMarketDataJob,
        HistoricalBackfillJob historicalBackfillJob,
        IMovingAverageService movingAverageService,
        StrategyEvaluationService strategyEvaluationService,
        SettingsValidationService settingsValidationService)
    {
        _clock = clock;
        _settingsStore = settingsStore;
        _crawlerRegistry = crawlerRegistry;
        _repository = repository;
        _marketDataRepository = marketDataRepository;
        _excelWorkbookService = excelWorkbookService;
        _userInteraction = userInteraction;
        _logger = logger;
        _dailyMarketDataJob = dailyMarketDataJob;
        _historicalBackfillJob = historicalBackfillJob;
        _movingAverageService = movingAverageService;
        _strategyEvaluationService = strategyEvaluationService;
        _settingsValidationService = settingsValidationService;
    }

    public bool IsRunning => _singleRunLock.CurrentCount == 0;

    public async Task<JobRunSummary> RunAsync(bool isManualRun, CancellationToken cancellationToken)
    {
        if (!await _singleRunLock.WaitAsync(0, cancellationToken).ConfigureAwait(false))
        {
            return CreateImmediateFailure("已有一個工作正在執行，已略過本次重複觸發。", RunOutcome.NonRetryableFailure);
        }

        var startedAt = _clock.GetTaipeiNow();
        var targetDate = DateOnly.FromDateTime(startedAt.DateTime);
        Guid jobId = Guid.Empty;
        var attemptNumber = 1;
        var totalCrawled = 0;
        var holdingCount = 0;
        IReadOnlyList<StrategyAlert> alerts = [];

        try
        {
            var settings = await _settingsStore.LoadAsync(cancellationToken).ConfigureAwait(false);
            _settingsValidationService.EnsureFixedSources(settings);
            var validationErrors = _settingsValidationService.Validate(settings);
            if (validationErrors.Count > 0)
            {
                throw new NonRetryableJobException(string.Join(Environment.NewLine, validationErrors));
            }

            attemptNumber = await _repository.GetAttemptCountAsync(targetDate, cancellationToken).ConfigureAwait(false) + 1;
            jobId = await _repository.BeginJobAsync(targetDate, attemptNumber, startedAt, cancellationToken).ConfigureAwait(false);

            // 步驟一：鉅亨網多頭／空頭排列完整清單（集中＋店頭），僅作清單保存與交叉驗證，不再是正式均價來源。
            // 均線正式判斷已改依 TWSE／TPEx 官方資料計算，鉅亨網本步驟採 best-effort：
            // 網站尚未更新或擷取失敗時，只記錄提醒訊息並繼續執行官方價格與策略流程，不得整批失敗。
            _userInteraction.ShowStatus($"正在擷取 {targetDate:yyyy-MM-dd} 鉅亨網多頭／空頭排列清單……");
            _logger.Info($"工作 {jobId} 開始。目標日期={targetDate:yyyy-MM-dd}，第 {attemptNumber} 次。");
            IReadOnlyList<CrawlBatch> cnyesBatches = [];
            string? cnyesReminder = null;
            try
            {
                cnyesBatches = await CrawlAllRequiredBatchesAsync(jobId, settings, targetDate, cancellationToken).ConfigureAwait(false);
                totalCrawled = cnyesBatches.Sum(x => x.Items.Count);
                await _repository.SaveCompleteTechnicalBatchAsync(jobId, cnyesBatches, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                cnyesReminder = $"鉅亨網多頭／空頭排列清單本次未成功更新，僅影響清單保存與交叉驗證，不影響均線與策略正式判斷（原因：{ex.Message}）。";
                _logger.Warning(cnyesReminder);
            }

            // 步驟二：TWSE／TPEx 官方每日收盤價；來源資料日期必須等於 targetDate 才可寫入正式資料。
            _userInteraction.ShowStatus($"正在擷取 {targetDate:yyyy-MM-dd} TWSE／TPEx 官方每日收盤價……");
            var priceBatches = await _dailyMarketDataJob.RunAsync(targetDate, settings.OfficialMarketData, cancellationToken).ConfigureAwait(false);
            EnsureOfficialPriceBatchesSucceeded(priceBatches, targetDate);

            if (priceBatches.All(x => x.Status == OfficialPriceBatchStatus.Holiday))
            {
                var holidayMessage = "今日為休市日，TWSE／TPEx 官方來源均無交易資料，本次不產生策略通知。";
                if (cnyesReminder is not null)
                {
                    holidayMessage += $" 提醒：{cnyesReminder}";
                }

                var holidaySummary = new JobRunSummary(
                    jobId, targetDate, JobStatus.NoTradingData, RunOutcome.NonRetryableFailure,
                    holidayMessage,
                    attemptNumber, totalCrawled, 0, 0, 0, startedAt, _clock.GetTaipeiNow(), []);
                await _repository.CompleteJobAsync(jobId, holidaySummary, cancellationToken).ConfigureAwait(false);
                _logger.Info($"工作 {jobId}：{holidaySummary.Message}");
                _userInteraction.ShowSuccess(holidaySummary);
                return holidaySummary;
            }

            // 步驟三：歷史資料回補（採 best-effort；個別股票即使回補失敗，仍以 InsufficientHistory 反映在均線結果，不阻擋整批）。
            try
            {
                _userInteraction.ShowStatus("正在檢查並回補 MA120 所需歷史資料……");
                await _historicalBackfillJob.RunAsync(targetDate, settings.OfficialMarketData, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.Warning($"歷史資料回補發生例外，本次僅記錄警告並繼續：{ex.Message}");
            }

            if (settings.ShowExcelSafetyPrompt)
            {
                var accepted = await _userInteraction.ConfirmExcelSafetyAsync(cancellationToken).ConfigureAwait(false);
                if (!accepted)
                {
                    throw new RetryableJobException("使用者尚未允許操作 Excel，稍後可重新執行。");
                }
            }

            _userInteraction.ShowStatus("正在讀取 Excel 客戶持股……");
            var holdings = await _excelWorkbookService.ReadHoldingsAsync(settings, targetDate, cancellationToken).ConfigureAwait(false);
            holdingCount = holdings.Count;

            // 步驟四：依官方收盤價計算 MA5／MA20／MA60／MA120（有效交易日，非日曆日）。
            _userInteraction.ShowStatus("正在計算均線……");
            var stockCodes = holdings.Select(x => StrategyEvaluationService.NormalizeStockCode(x.StockCode)).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
            var movingAverages = await _movingAverageService.CalculateManyAsync(stockCodes, targetDate, cancellationToken).ConfigureAwait(false);
            await _marketDataRepository.SaveMovingAverageResultsAsync(targetDate, movingAverages, cancellationToken).ConfigureAwait(false);
            var marketTypes = await _marketDataRepository.GetStockMarketTypesAsync(stockCodes, cancellationToken).ConfigureAwait(false);

            CrossValidateWithCnyes(cnyesBatches, movingAverages);

            var calculatedAt = _clock.GetTaipeiNow();
            alerts = _strategyEvaluationService.Evaluate(targetDate, holdings, movingAverages, marketTypes, calculatedAt);

            await _repository.SaveHoldingsAndAlertsAsync(jobId, targetDate, settings.WorkbookPath, holdings, alerts, cancellationToken).ConfigureAwait(false);

            _userInteraction.ShowStatus("正在寫入「每日五日均價策略」頁籤……");
            await _excelWorkbookService.WriteStrategyResultsAsync(settings, targetDate, alerts, cancellationToken).ConfigureAwait(false);

            var completedAt = _clock.GetTaipeiNow();
            var successMessage = $"完成：鉅亨清單 {totalCrawled} 筆、持股 {holdingCount} 筆、策略通知 {alerts.Count(x => x.AlertKind == AlertKind.MovingAverageTriggered)} 筆。均價來源：TWSE／TPEx 官方收盤價。";
            if (cnyesReminder is not null)
            {
                successMessage += $" 提醒：{cnyesReminder}";
            }

            var success = new JobRunSummary(
                jobId,
                targetDate,
                JobStatus.Succeeded,
                RunOutcome.Success,
                successMessage,
                attemptNumber,
                totalCrawled,
                holdingCount,
                alerts.Count(x => x.AlertKind == AlertKind.MovingAverageTriggered),
                alerts.Count(x => x.AlertKind == AlertKind.TechnicalIndicatorMissing),
                startedAt,
                completedAt,
                alerts);

            await _repository.CompleteJobAsync(jobId, success, cancellationToken).ConfigureAwait(false);
            _logger.Info($"工作 {jobId} 成功：{success.Message}");
            _userInteraction.ShowSuccess(success);
            return success;
        }
        catch (WebsiteNotUpdatedException ex)
        {
            return await CompleteFailureAsync(jobId, targetDate, attemptNumber, startedAt, totalCrawled, holdingCount,
                alerts, JobStatus.WebsiteNotUpdated, RunOutcome.RetryableFailure, ex.Message, ex, cancellationToken).ConfigureAwait(false);
        }
        catch (RetryableExcelJobException ex)
        {
            return await CompleteFailureAsync(jobId, targetDate, attemptNumber, startedAt, totalCrawled, holdingCount,
                alerts, ex.Status, RunOutcome.RetryableFailure, ex.Message, ex, cancellationToken).ConfigureAwait(false);
        }
        catch (NonRetryableExcelJobException ex)
        {
            return await CompleteFailureAsync(jobId, targetDate, attemptNumber, startedAt, totalCrawled, holdingCount,
                alerts, ex.Status, RunOutcome.NonRetryableFailure, ex.Message, ex, cancellationToken).ConfigureAwait(false);
        }
        catch (RetryableJobException ex)
        {
            return await CompleteFailureAsync(jobId, targetDate, attemptNumber, startedAt, totalCrawled, holdingCount,
                alerts, JobStatus.CrawlFailed, RunOutcome.RetryableFailure, ex.Message, ex, cancellationToken).ConfigureAwait(false);
        }
        catch (NonRetryableJobException ex)
        {
            return await CompleteFailureAsync(jobId, targetDate, attemptNumber, startedAt, totalCrawled, holdingCount,
                alerts, JobStatus.ValidationFailed, RunOutcome.NonRetryableFailure, ex.Message, ex, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return await CompleteFailureAsync(jobId, targetDate, attemptNumber, startedAt, totalCrawled, holdingCount,
                alerts, JobStatus.Cancelled, RunOutcome.NonRetryableFailure, "工作已取消。", null, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            return await CompleteFailureAsync(jobId, targetDate, attemptNumber, startedAt, totalCrawled, holdingCount,
                alerts, JobStatus.CrawlFailed, RunOutcome.RetryableFailure, $"未預期錯誤：{ex.Message}", ex, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _singleRunLock.Release();
        }
    }

    /// <summary>
    /// 驗證 TWSE、TPEx 官方每日收盤價批次皆已成功；任一來源尚未公布當日資料或失敗時一律拒絕繼續，
    /// 絕不允許以前一交易日資料頂替，也不得混用不同日期的官方價格繼續計算策略。
    /// </summary>
    private static void EnsureOfficialPriceBatchesSucceeded(IReadOnlyList<OfficialPriceBatchSummary> priceBatches, DateOnly targetDate)
    {
        if (priceBatches.All(x => x.Status == OfficialPriceBatchStatus.Holiday))
        {
            return;
        }

        var notPublished = priceBatches.FirstOrDefault(x => x.Status == OfficialPriceBatchStatus.NotPublished);
        if (notPublished is not null)
        {
            throw new WebsiteNotUpdatedException(
                $"{notPublished.SourceProvider} 官方每日收盤價尚未更新為 {targetDate:yyyy-MM-dd}：{notPublished.ErrorMessage}");
        }

        var failed = priceBatches.FirstOrDefault(x => x.Status is OfficialPriceBatchStatus.Failed or OfficialPriceBatchStatus.PartialFailed);
        if (failed is not null)
        {
            throw new RetryableJobException($"{failed.SourceProvider} 官方每日收盤價擷取失敗：{failed.ErrorMessage}");
        }

        var incomplete = priceBatches.FirstOrDefault(x => x.Status != OfficialPriceBatchStatus.Succeeded);
        if (incomplete is not null)
        {
            throw new RetryableJobException($"{incomplete.SourceProvider} 官方每日收盤價批次狀態異常（{incomplete.Status}），暫不繼續策略計算。");
        }
    }

    /// <summary>
    /// 官方均線與鉅亨網多頭／空頭排列既有 5／20／60／120 日均價的輕量交叉驗證；僅記錄差異供人工追查，
    /// 不影響正式判斷，也不寫入額外資料表（差異僅出現在 Log）。
    /// </summary>
    private void CrossValidateWithCnyes(IReadOnlyList<CrawlBatch> cnyesBatches, IReadOnlyList<MovingAverageResult> officialResults)
    {
        var officialByCode = officialResults
            .Where(x => x.MovingAverage20 is not null)
            .GroupBy(x => StrategyEvaluationService.NormalizeStockCode(x.StockCode), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);

        foreach (var item in cnyesBatches.SelectMany(batch => batch.Items))
        {
            var code = StrategyEvaluationService.NormalizeStockCode(item.StockCode);
            if (!officialByCode.TryGetValue(code, out var official) || official.MovingAverage20 is not decimal officialMa20)
            {
                continue;
            }

            var diff = Math.Abs(officialMa20 - item.MovingAverage20);
            var tolerance = Math.Max(0.5m, item.MovingAverage20 * 0.02m);
            if (diff > tolerance)
            {
                _logger.Warning(
                    $"交叉驗證差異：股票 {item.StockCode} 官方 20 日均價={officialMa20:0.00}，鉅亨網 20 日均價={item.MovingAverage20:0.00}，" +
                    $"差異 {diff:0.00} 已超出容忍範圍，僅記錄不影響正式判斷。");
            }
        }
    }

    private async Task<IReadOnlyList<CrawlBatch>> CrawlAllRequiredBatchesAsync(
        Guid jobId,
        AppSettings settings,
        DateOnly targetDate,
        CancellationToken cancellationToken)
    {
        var sources = settings.Sources.Where(x => x.Enabled).Select(x => x.ToDomain()).ToList();
        var batches = new List<CrawlBatch>();

        foreach (var source in sources)
        {
            ISourceCrawler crawler;
            try
            {
                crawler = _crawlerRegistry.Resolve(source.ProviderKey);
            }
            catch (Exception ex)
            {
                if (source.Required)
                {
                    throw new NonRetryableJobException($"必要來源尚未有可用爬蟲：{source.DisplayName}。", ex);
                }

                _logger.Warning($"略過尚未實作 Provider 的非必要來源：{source.DisplayName} ({source.ProviderKey})");
                continue;
            }

            // 同一來源的集中／店頭必須一起成功，避免非必要來源只保存半份資料。
            var sourceBatches = new List<CrawlBatch>(2);
            var sourceFailed = false;
            foreach (var market in new[] { MarketType.Listed, MarketType.Otc })
            {
                var detailStartedAt = _clock.GetTaipeiNow();
                try
                {
                    var batch = await crawler.CrawlAsync(source, market, targetDate, settings, cancellationToken).ConfigureAwait(false);
                    ValidateBatch(batch, targetDate);
                    sourceBatches.Add(batch);
                    await _repository.RecordJobDetailAsync(jobId, batch, "Validated", null, cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    await _repository.RecordJobDetailFailureAsync(
                        jobId, source, market, targetDate, "Failed", ex.Message,
                        detailStartedAt, _clock.GetTaipeiNow(), cancellationToken).ConfigureAwait(false);

                    if (source.Required)
                    {
                        throw;
                    }

                    sourceFailed = true;
                    _logger.Warning($"非必要來源 {source.DisplayName}／{ToMarketText(market)} 失敗，該來源本次不寫入正式資料：{ex.Message}");
                    break;
                }
            }

            if (!sourceFailed && sourceBatches.Count == 2)
            {
                batches.AddRange(sourceBatches);
            }
        }

        var requiredSources = sources.Count(x => x.Required);
        var expectedRequiredBatchCount = requiredSources * 2;
        var actualRequiredBatchCount = batches.Count(x => x.Source.Required);
        if (actualRequiredBatchCount != expectedRequiredBatchCount)
        {
            throw new NonRetryableJobException(
                $"必要批次不完整，應有 {expectedRequiredBatchCount} 批，實際只有 {actualRequiredBatchCount} 批。");
        }

        return batches;
    }

    private static void ValidateBatch(CrawlBatch batch, DateOnly targetDate)
    {
        if (batch.TargetDate != targetDate || batch.PageDate != targetDate)
        {
            throw new WebsiteNotUpdatedException(
                $"{batch.Source.DisplayName}／{ToMarketText(batch.MarketType)} 頁面日期為 {batch.PageDate:yyyy-MM-dd}，尚未更新為 {targetDate:yyyy-MM-dd}。禁止改抓前一交易日。");
        }

        if (batch.Items.Count == 0 && !batch.IsExplicitNoData)
        {
            throw new RetryableJobException(
                $"{batch.Source.DisplayName}／{ToMarketText(batch.MarketType)} 回傳零筆，但頁面沒有明確的無資料訊息，視為解析失敗。");
        }

        if (batch.Items.Any(x => x.TradeDate != targetDate
                                 || x.IndicatorType != batch.Source.IndicatorType
                                 || x.MarketType != batch.MarketType
                                 || string.IsNullOrWhiteSpace(x.StockCode)
                                 || string.IsNullOrWhiteSpace(x.StockName)))
        {
            throw new NonRetryableJobException(
                $"{batch.Source.DisplayName}／{ToMarketText(batch.MarketType)} 內容驗證失敗，禁止寫入正式資料表。");
        }

        var duplicateCodes = batch.Items
            .GroupBy(x => x.StockCode, StringComparer.OrdinalIgnoreCase)
            .Where(x => x.Count() > 1)
            .Select(x => x.Key)
            .Take(10)
            .ToArray();
        if (duplicateCodes.Length > 0)
        {
            throw new NonRetryableJobException(
                $"{batch.Source.DisplayName}／{ToMarketText(batch.MarketType)} 出現重複股票代碼：{string.Join("、", duplicateCodes)}。");
        }
    }

    private async Task<JobRunSummary> CompleteFailureAsync(
        Guid jobId,
        DateOnly targetDate,
        int attemptNumber,
        DateTimeOffset startedAt,
        int crawledCount,
        int holdingCount,
        IReadOnlyList<StrategyAlert> alerts,
        JobStatus status,
        RunOutcome outcome,
        string message,
        Exception? exception,
        CancellationToken cancellationToken)
    {
        var summary = new JobRunSummary(
            jobId == Guid.Empty ? Guid.NewGuid() : jobId,
            targetDate,
            status,
            outcome,
            message,
            attemptNumber,
            crawledCount,
            holdingCount,
            alerts.Count(x => x.AlertKind == AlertKind.MovingAverageTriggered),
            alerts.Count(x => x.AlertKind == AlertKind.TechnicalIndicatorMissing),
            startedAt,
            _clock.GetTaipeiNow(),
            alerts);

        if (jobId != Guid.Empty)
        {
            try
            {
                await _repository.CompleteJobAsync(jobId, summary, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception repositoryException)
            {
                _logger.Error($"工作 {jobId} 失敗狀態寫入資料庫時再次失敗。", repositoryException);
            }
        }

        _logger.Error($"工作 {summary.JobId} 失敗：{message}", exception);
        _userInteraction.ShowFailure(summary);
        return summary;
    }

    private JobRunSummary CreateImmediateFailure(string message, RunOutcome outcome)
    {
        var now = _clock.GetTaipeiNow();
        return new JobRunSummary(Guid.NewGuid(), DateOnly.FromDateTime(now.DateTime), JobStatus.ValidationFailed,
            outcome, message, 0, 0, 0, 0, 0, now, now, []);
    }

    private static string ToMarketText(MarketType marketType) => marketType == MarketType.Listed ? "集中市場" : "店頭市場";
}
