using YiHeLee.Application.Abstractions;
using YiHeLee.Application.Exceptions;
using YiHeLee.Domain;

namespace YiHeLee.Application.Services;

/// <summary>
/// 收盤後官方收盤價與均價前置更新（2026-07-13 由原 DailyJobService 拆分而來）。
/// 只負責：13:35 收盤更新、TWSE／TPEx／TPEx 興櫃官方收盤行情、來源日期驗證、
/// StockDailyPrice Upsert、計算及保存 StockMovingAverage、鉅亨清單保存與交叉驗證、
/// Excel「每日五日均價策略」七欄輸出、收盤 Job／錯誤／重試紀錄（JobRuns），
/// 並在完成後把該交易日標記為「下一交易日盤中判斷基準已準備完成」（以已保存的均價快照為準）。
/// 禁止包含：讀取客戶頁籤現價、盤中監控、呼叫客戶盤中策略通知（StrategyEvaluationService）；
/// 也不得因 DDE 或進場價異常導致收盤工作失敗——本類別完全不接觸客戶持股欄位。
/// 當天收盤產生的新均價，下一個交易日盤中才開始使用。
/// </summary>
public sealed class ClosePrecomputeJobService
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
    private readonly IMarketPriceService _marketPriceService;
    private readonly IMovingAverageService _movingAverageService;
    private readonly SettingsValidationService _settingsValidationService;
    private readonly IWorkflowExecutionGate _executionGate;
    private readonly SemaphoreSlim _singleRunLock = new(1, 1);

    public ClosePrecomputeJobService(
        IClock clock,
        ISettingsStore settingsStore,
        ICrawlerRegistry crawlerRegistry,
        IYiHeLeeRepository repository,
        IMarketDataRepository marketDataRepository,
        IExcelWorkbookService excelWorkbookService,
        IUserInteraction userInteraction,
        IAppLogger logger,
        DailyMarketDataJob dailyMarketDataJob,
        IMarketPriceService marketPriceService,
        IMovingAverageService movingAverageService,
        SettingsValidationService settingsValidationService,
        IWorkflowExecutionGate executionGate)
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
        _marketPriceService = marketPriceService;
        _movingAverageService = movingAverageService;
        _settingsValidationService = settingsValidationService;
        _executionGate = executionGate;
    }

    public bool IsRunning => _singleRunLock.CurrentCount == 0;

    public async Task<JobRunSummary> RunAsync(bool isManualRun, CancellationToken cancellationToken, DateOnly? manualTargetDate = null)
    {
        if (!await _singleRunLock.WaitAsync(0, cancellationToken).ConfigureAwait(false))
        {
            return CreateImmediateFailure("已有一個收盤更新正在執行，已略過本次重複觸發。", RunOutcome.NonRetryableFailure);
        }

        try
        {
            // 共用流程協調鎖：等待目前盤中判斷結束後開始；期間新的盤中 Tick 會被擋下並記錄略過，
            // 確保 Excel COM 操作與均價快照寫入不會和盤中流程同時執行。
            using var gateTicket = await _executionGate.EnterAsync("收盤更新", cancellationToken).ConfigureAwait(false);
            return await RunCoreAsync(isManualRun, manualTargetDate, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _singleRunLock.Release();
        }
    }

    private async Task<JobRunSummary> RunCoreAsync(bool isManualRun, DateOnly? manualTargetDate, CancellationToken cancellationToken)
    {
        var startedAt = _clock.GetTaipeiNow();
        var targetDate = manualTargetDate ?? DateOnly.FromDateTime(startedAt.DateTime);
        Guid jobId = Guid.Empty;
        var attemptNumber = 1;
        var totalCrawled = 0;
        IReadOnlyList<DailyMovingAverageSnapshot> movingAverageAnomalies = [];
        IReadOnlyList<DailyMovingAverageSnapshot> movingAverageSnapshots = [];

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

            _logger.Info($"收盤更新 {jobId} 開始。目標日期={targetDate:yyyy-MM-dd}，第 {attemptNumber} 次。");
            IReadOnlyList<CrawlBatch> cnyesBatches = [];
            string? cnyesReminder = null;

            // 步驟一：TWSE／TPEx 官方每日收盤價；來源資料日期必須等於 targetDate 才可寫入正式資料。
            // 本系統以官方收盤價與 DB 歷史資料作為正式 MA 來源，鉅亨網只在均線算完後作清單保存與交叉比對。
            _userInteraction.ShowStatus($"正在擷取 {targetDate:yyyy-MM-dd} TWSE／TPEx 官方每日收盤價……", 10);
            var priceBatches = await _dailyMarketDataJob.RunAsync(targetDate, settings.OfficialMarketData, cancellationToken).ConfigureAwait(false);
            EnsureOfficialPriceBatchesSucceeded(priceBatches, targetDate);

            if (priceBatches.All(x => x.Status == OfficialPriceBatchStatus.Holiday))
            {
                var holidayMessage = "今日為休市日，TWSE／TPEx 官方來源均無交易資料，本次不更新均價基準。";
                var holidaySummary = new JobRunSummary(
                    jobId, targetDate, JobStatus.NoTradingData, RunOutcome.NonRetryableFailure,
                    holidayMessage,
                    attemptNumber, totalCrawled, 0, 0, 0, startedAt, _clock.GetTaipeiNow(), [], []);
                await _repository.CompleteJobAsync(jobId, holidaySummary, cancellationToken).ConfigureAwait(false);
                _logger.Info($"收盤更新 {jobId}：{holidaySummary.Message}");
                _userInteraction.ShowSuccess(holidaySummary);
                return holidaySummary;
            }

            // 步驟一之二：TPEx 興櫃股票當日行情。本端點沒有日期參數、無法歷史回補，只能逐日累積；
            // 採 best-effort，失敗只記錄提醒，不得影響上市／上櫃已驗證成功的正式均價前置。
            string? emergingReminder = null;
            try
            {
                _userInteraction.ShowStatus($"正在擷取 {targetDate:yyyy-MM-dd} TPEx 興櫃股票當日行情……", 20);
                var emergingSummary = await _marketPriceService.FetchAndSaveSingleAsync(
                    OfficialPriceJobType.DailyMarketData, targetDate, MarketType.Emerging, settings.OfficialMarketData, cancellationToken).ConfigureAwait(false);
                if (emergingSummary.Status is not (OfficialPriceBatchStatus.Succeeded or OfficialPriceBatchStatus.Holiday))
                {
                    emergingReminder = $"TPEx 興櫃股票當日行情本次未成功更新，僅影響興櫃股票的均價基準，不影響上市／上櫃正式資料（原因：{emergingSummary.ErrorMessage}）。";
                    _logger.Warning(emergingReminder);
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                emergingReminder = $"TPEx 興櫃股票當日行情本次未成功更新，僅影響興櫃股票的均價基準，不影響上市／上櫃正式資料（原因：{ex.Message}）。";
                _logger.Warning(emergingReminder);
            }

            // 每日五日均價策略是純 DB 前置作業：只根據當日已保存的官方收盤價股票全集計算並保存
            // 代碼、名稱、收盤價、MA5、MA20、MA60、MA120。不得依賴客戶持股、Excel 現價或 DDE 狀態。
            var precomputeStockCodes = (await _marketDataRepository.GetStockCodesWithDailyPriceAsync(targetDate, cancellationToken).ConfigureAwait(false))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
            if (precomputeStockCodes.Length == 0)
            {
                throw new RetryableJobException($"{targetDate:yyyy-MM-dd} 已通過官方批次檢查，但 DB 沒有任何當日收盤價可供均線前置計算。");
            }

            // 步驟二：依 DB 目前已有的官方收盤價計算 MA5／MA20／MA60／MA120（有效交易日，非日曆日）。
            // 每日均價前置作業只負責「用既有 DB 資料換算並保存」；資料不足時存成異常，
            // 交由 WinForms「均價資料異常」頁籤告知使用者，不得在收盤更新中一路往前自動回補。
            _userInteraction.ShowStatus("正在依 DB 既有官方收盤價計算均線……", 55);
            var movingAverages = (await _movingAverageService.CalculateManyAsync(precomputeStockCodes, targetDate, cancellationToken).ConfigureAwait(false)).ToArray();
            await _marketDataRepository.SaveMovingAverageResultsAsync(targetDate, movingAverages, cancellationToken).ConfigureAwait(false);
            movingAverages = (await _marketDataRepository.GetMovingAverageResultsAsync(targetDate, cancellationToken).ConfigureAwait(false)).ToArray();
            movingAverageSnapshots = await _marketDataRepository.GetMovingAverageSnapshotsAsync(targetDate, cancellationToken).ConfigureAwait(false);
            movingAverageAnomalies = await _marketDataRepository.GetMovingAverageAnomaliesAsync(targetDate, cancellationToken).ConfigureAwait(false);

            // 步驟三：鉅亨網多頭／空頭排列完整清單（集中＋店頭），只在官方均線算完後作清單保存與交叉驗證。
            // 網站尚未更新或擷取失敗時，只記錄提醒訊息，不影響已由 DB 官方收盤價算出的正式均線。
            _userInteraction.ShowStatus(
                settings.EnableCnyesMovingAverageComparison
                    ? $"正在擷取 {targetDate:yyyy-MM-dd} 鉅亨網清單並做最後比對……"
                    : $"正在擷取 {targetDate:yyyy-MM-dd} 鉅亨網清單……",
                80);
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
                cnyesReminder = $"鉅亨網多頭／空頭排列清單本次未成功更新，僅影響清單保存與交叉驗證，不影響均線正式資料（原因：{ex.Message}）。";
                _logger.Warning(cnyesReminder);
            }

            if (settings.EnableCnyesMovingAverageComparison)
            {
                _ = CrossValidateWithCnyes(cnyesBatches, movingAverages);
            }
            else
            {
                _logger.Info("設定已關閉鉅亨網址均價比對；本次略過鉅亨 MA 與官方 MA 交叉驗證。");
            }

            if (settings.ShowExcelSafetyPrompt)
            {
                var accepted = await _userInteraction.ConfirmExcelSafetyAsync(cancellationToken).ConfigureAwait(false);
                if (!accepted)
                {
                    throw new RetryableJobException("使用者尚未允許操作 Excel，稍後可重新執行。");
                }
            }

            // 步驟四：「每日五日均價策略」頁籤只保存代碼、名稱、收盤價與 MA5／MA20／MA60／MA120；
            // 客戶、DDE 現價、觸發條件與診斷資訊屬於盤中監控的中央結果頁籤，不寫入此頁籤。
            // 寫入前仍依既有規則備份活頁簿（由 ExcelWorkbookService 處理）。
            _userInteraction.ShowStatus("正在寫入「每日五日均價策略」頁籤……", 95);
            await _excelWorkbookService.WriteStrategyResultsAsync(settings, targetDate, movingAverageSnapshots, cancellationToken).ConfigureAwait(false);

            var completedAt = _clock.GetTaipeiNow();
            var successMessage =
                $"收盤更新完成：鉅亨清單 {totalCrawled} 筆、均價前置 {movingAverages.Length} 檔（BaselineTradeDate={targetDate:yyyy-MM-dd}）。" +
                "均價來源：TWSE／TPEx／TPEx興櫃 官方收盤價。本日均價已準備完成，下一個交易日盤中才開始使用；" +
                "收盤流程不讀取客戶現價、不執行盤中策略判斷。";
            if (!settings.EnableCnyesMovingAverageComparison)
            {
                successMessage += " 鉅亨網址均價比對已依設定略過。";
            }
            if (cnyesReminder is not null)
            {
                successMessage += $" 提醒：{cnyesReminder}";
            }

            if (emergingReminder is not null)
            {
                successMessage += $" 提醒：{emergingReminder}";
            }

            var success = new JobRunSummary(
                jobId,
                targetDate,
                JobStatus.Succeeded,
                RunOutcome.Success,
                successMessage,
                attemptNumber,
                totalCrawled,
                0,
                0,
                0,
                startedAt,
                completedAt,
                [],
                movingAverageAnomalies);

            await _repository.CompleteJobAsync(jobId, success, cancellationToken).ConfigureAwait(false);
            _logger.Info($"收盤更新 {jobId} 成功：{success.Message}");
            _userInteraction.ShowSuccess(success);
            return success;
        }
        catch (WebsiteNotUpdatedException ex)
        {
            return await CompleteFailureAsync(jobId, targetDate, attemptNumber, startedAt, totalCrawled,
                movingAverageAnomalies, JobStatus.WebsiteNotUpdated, RunOutcome.RetryableFailure, ex.Message, ex, cancellationToken).ConfigureAwait(false);
        }
        catch (RetryableExcelJobException ex)
        {
            return await CompleteFailureAsync(jobId, targetDate, attemptNumber, startedAt, totalCrawled,
                movingAverageAnomalies, ex.Status, RunOutcome.RetryableFailure, ex.Message, ex, cancellationToken).ConfigureAwait(false);
        }
        catch (NonRetryableExcelJobException ex)
        {
            return await CompleteFailureAsync(jobId, targetDate, attemptNumber, startedAt, totalCrawled,
                movingAverageAnomalies, ex.Status, RunOutcome.NonRetryableFailure, ex.Message, ex, cancellationToken).ConfigureAwait(false);
        }
        catch (RetryableJobException ex)
        {
            return await CompleteFailureAsync(jobId, targetDate, attemptNumber, startedAt, totalCrawled,
                movingAverageAnomalies, JobStatus.CrawlFailed, RunOutcome.RetryableFailure, ex.Message, ex, cancellationToken).ConfigureAwait(false);
        }
        catch (NonRetryableJobException ex)
        {
            return await CompleteFailureAsync(jobId, targetDate, attemptNumber, startedAt, totalCrawled,
                movingAverageAnomalies, JobStatus.ValidationFailed, RunOutcome.NonRetryableFailure, ex.Message, ex, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return await CompleteFailureAsync(jobId, targetDate, attemptNumber, startedAt, totalCrawled,
                movingAverageAnomalies, JobStatus.Cancelled, RunOutcome.NonRetryableFailure, "工作已取消。", null, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            return await CompleteFailureAsync(jobId, targetDate, attemptNumber, startedAt, totalCrawled,
                movingAverageAnomalies, JobStatus.CrawlFailed, RunOutcome.RetryableFailure, $"未預期錯誤：{ex.Message}", ex, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// 驗證 TWSE、TPEx 官方每日收盤價批次皆已成功；任一來源尚未公布當日資料或失敗時一律拒絕繼續，
    /// 絕不允許以前一交易日資料頂替，也不得混用不同日期的官方價格繼續計算均線。
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
            throw new RetryableJobException($"{incomplete.SourceProvider} 官方每日收盤價批次狀態異常（{incomplete.Status}），暫不繼續均價前置計算。");
        }
    }

    /// <summary>
    /// 官方均線與鉅亨網多頭／空頭排列既有 5／20／60／120 日均價的輕量交叉驗證；僅供人工追查、
    /// 中央結果診斷與 Log 參考，不影響正式資料，也不覆蓋官方資料。
    /// </summary>
    private Dictionary<string, string> CrossValidateWithCnyes(IReadOnlyList<CrawlBatch> cnyesBatches, IReadOnlyList<MovingAverageResult> officialResults)
    {
        var statuses = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var officialByCode = officialResults
            .GroupBy(x => StrategyEvaluationService.NormalizeStockCode(x.StockCode), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);

        if (cnyesBatches.Count == 0)
        {
            // 鉅亨網本次未成功更新（best-effort 失敗），不影響官方資料，僅標示無法比對。
            foreach (var code in officialByCode.Keys)
            {
                statuses[code] = "鉅亨網本次無資料";
            }

            return statuses;
        }

        var cnyesByCode = cnyesBatches
            .SelectMany(batch => batch.Items)
            .GroupBy(x => StrategyEvaluationService.NormalizeStockCode(x.StockCode), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);

        foreach (var (code, official) in officialByCode)
        {
            if (!cnyesByCode.TryGetValue(code, out var cnyesItem))
            {
                statuses[code] = "不適用（未出現在鉅亨清單）";
                continue;
            }

            if (official.MovingAverage20 is not decimal officialMa20)
            {
                statuses[code] = "資料不足，暫不比對";
                continue;
            }

            var diff = Math.Abs(officialMa20 - cnyesItem.MovingAverage20);
            var tolerance = Math.Max(0.5m, cnyesItem.MovingAverage20 * 0.02m);
            if (diff > tolerance)
            {
                statuses[code] = $"差異 {diff:0.00}";
                _logger.Warning(
                    $"交叉驗證差異：股票 {cnyesItem.StockCode} 官方 20 日均價={officialMa20:0.00}，鉅亨網 20 日均價={cnyesItem.MovingAverage20:0.00}，" +
                    $"差異 {diff:0.00} 已超出容忍範圍，僅記錄不影響正式資料。");
            }
            else
            {
                statuses[code] = "相符";
            }
        }

        return statuses;
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
        IReadOnlyList<DailyMovingAverageSnapshot> movingAverageAnomalies,
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
            0,
            0,
            0,
            startedAt,
            _clock.GetTaipeiNow(),
            [],
            movingAverageAnomalies);

        if (jobId != Guid.Empty)
        {
            try
            {
                await _repository.CompleteJobAsync(jobId, summary, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception repositoryException)
            {
                _logger.Error($"收盤更新 {jobId} 失敗狀態寫入資料庫時再次失敗。", repositoryException);
            }
        }

        _logger.Error($"收盤更新 {summary.JobId} 失敗：{message}", exception);
        _userInteraction.ShowFailure(summary);
        return summary;
    }

    private JobRunSummary CreateImmediateFailure(string message, RunOutcome outcome)
    {
        var now = _clock.GetTaipeiNow();
        return new JobRunSummary(Guid.NewGuid(), DateOnly.FromDateTime(now.DateTime), JobStatus.ValidationFailed,
            outcome, message, 0, 0, 0, 0, 0, now, now, [], []);
    }

    private static string ToMarketText(MarketType marketType) => marketType == MarketType.Listed ? "集中市場" : "店頭市場";
}
