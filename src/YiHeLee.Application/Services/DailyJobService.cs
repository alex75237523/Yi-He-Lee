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
    private readonly IExcelWorkbookService _excelWorkbookService;
    private readonly IUserInteraction _userInteraction;
    private readonly IAppLogger _logger;
    private readonly StrategyEvaluationService _strategyEvaluationService;
    private readonly SettingsValidationService _settingsValidationService;
    private readonly SemaphoreSlim _singleRunLock = new(1, 1);

    public DailyJobService(
        IClock clock,
        ISettingsStore settingsStore,
        ICrawlerRegistry crawlerRegistry,
        IYiHeLeeRepository repository,
        IExcelWorkbookService excelWorkbookService,
        IUserInteraction userInteraction,
        IAppLogger logger,
        StrategyEvaluationService strategyEvaluationService,
        SettingsValidationService settingsValidationService)
    {
        _clock = clock;
        _settingsStore = settingsStore;
        _crawlerRegistry = crawlerRegistry;
        _repository = repository;
        _excelWorkbookService = excelWorkbookService;
        _userInteraction = userInteraction;
        _logger = logger;
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

            _userInteraction.ShowStatus($"正在擷取 {targetDate:yyyy-MM-dd} 技術指標資料……");
            _logger.Info($"工作 {jobId} 開始。目標日期={targetDate:yyyy-MM-dd}，第 {attemptNumber} 次。");

            var batches = await CrawlAllRequiredBatchesAsync(jobId, settings, targetDate, cancellationToken).ConfigureAwait(false);
            totalCrawled = batches.Sum(x => x.Items.Count);

            // 四個必要批次全數驗證通過後，才以單一 SQLite transaction 寫入正式資料。
            await _repository.SaveCompleteTechnicalBatchAsync(jobId, batches, cancellationToken).ConfigureAwait(false);

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

            var indicators = await _repository.GetTechnicalIndicatorsAsync(targetDate, cancellationToken).ConfigureAwait(false);
            alerts = _strategyEvaluationService.Evaluate(targetDate, holdings, indicators);

            await _repository.SaveHoldingsAndAlertsAsync(jobId, targetDate, settings.WorkbookPath, holdings, alerts, cancellationToken).ConfigureAwait(false);

            _userInteraction.ShowStatus("正在寫入「每日五日均價策略」頁籤……");
            await _excelWorkbookService.WriteStrategyResultsAsync(settings, targetDate, alerts, cancellationToken).ConfigureAwait(false);

            var completedAt = _clock.GetTaipeiNow();
            var success = new JobRunSummary(
                jobId,
                targetDate,
                JobStatus.Succeeded,
                RunOutcome.Success,
                $"完成：擷取 {totalCrawled} 筆、持股 {holdingCount} 筆、策略通知 {alerts.Count(x => x.AlertKind == AlertKind.MovingAverageTriggered)} 筆。",
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
