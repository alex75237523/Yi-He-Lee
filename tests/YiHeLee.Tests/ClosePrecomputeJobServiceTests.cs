using YiHeLee.Application.Abstractions;
using YiHeLee.Application.Services;
using YiHeLee.Domain;

namespace YiHeLee.Tests;

/// <summary>
/// 驗證收盤後官方收盤價與均價前置更新（2026-07-13 盤中／收盤流程拆分）的流程隔離：
/// 收盤流程會抓正式收盤價、計算並保存均價、寫入 Excel 七欄頁籤；
/// 收盤流程不讀取客戶現價、不呼叫盤中策略判斷；DDE 異常不可能影響收盤流程
/// （本測試的 Excel 假物件在讀取持股時直接擲出例外，收盤流程仍必須成功）。
/// </summary>
public sealed class ClosePrecomputeJobServiceTests : IDisposable
{
    private static readonly DateOnly TradeDate = new(2026, 7, 13);
    private readonly string _workbookPath = Path.Combine(Path.GetTempPath(), $"yihelee-close-test-{Guid.NewGuid():N}.xlsx");

    public ClosePrecomputeJobServiceTests() => File.WriteAllBytes(_workbookPath, []);

    public void Dispose()
    {
        if (File.Exists(_workbookPath))
        {
            File.Delete(_workbookPath);
        }
    }

    [Fact]
    public async Task 收盤流程會抓正式收盤價_計算並保存均價_寫入Excel七欄頁籤()
    {
        var maByCode = new Dictionary<string, MovingAverageResult>(StringComparer.OrdinalIgnoreCase)
        {
            ["2330"] = FullHistoryMovingAverage("2330", close: 900m, ma5: 890m, ma20: 880m, ma60: 870m, ma120: 860m)
        };
        var (service, excel, marketPrice, movingAverage, _, marketDataRepository, callOrder) = CreateService(maByCode);

        var summary = await service.RunAsync(isManualRun: true, CancellationToken.None, TradeDate);

        Assert.Equal(RunOutcome.Success, summary.Outcome);
        Assert.True(marketPrice.FetchDailyCallCount >= 1, "收盤流程必須抓取 TWSE／TPEx 正式收盤價。");
        Assert.True(movingAverage.CallCount >= 1, "收盤流程必須計算均價。");
        Assert.Single(marketDataRepository.SavedMovingAverages, x => x.StockCode == "2330" && x.MovingAverage5 == 890m);
        Assert.Equal(1, excel.WriteCallCount);

        var row = Assert.Single(excel.WrittenResults!);
        Assert.Equal("2330", row.StockCode);
        Assert.Equal(900m, row.ClosePrice);
        Assert.Equal(890m, row.MovingAverage5);
        Assert.Equal(880m, row.MovingAverage20);
        Assert.Equal(870m, row.MovingAverage60);
        Assert.Equal(860m, row.MovingAverage120);

        // 官方擷取 → 均線計算 → 鉅亨清單 → Excel 寫入的順序不變，Excel 寫入仍是最後一步。
        Assert.True(callOrder.IndexOf("FetchDailyPrices") < callOrder.IndexOf("CalculateMovingAverages"));
        Assert.True(callOrder.IndexOf("CalculateMovingAverages") < callOrder.IndexOf("CrawlCnyes"));
        Assert.True(callOrder.IndexOf("CrawlCnyes") < callOrder.IndexOf("WriteResults"));
        Assert.Equal(callOrder.Count - 1, callOrder.IndexOf("WriteResults"));
    }

    [Fact]
    public async Task 關閉鉅亨網址均價比對時_仍保留鉅亨清單保存但完成訊息標示略過比對()
    {
        var maByCode = new Dictionary<string, MovingAverageResult>(StringComparer.OrdinalIgnoreCase)
        {
            ["2330"] = FullHistoryMovingAverage("2330", close: 900m, ma5: 890m, ma20: 880m, ma60: 870m, ma120: 860m)
        };
        var (service, _, _, _, _, _, callOrder) = CreateService(
            maByCode,
            configureSettings: settings => settings.EnableCnyesMovingAverageComparison = false);

        var summary = await service.RunAsync(isManualRun: true, CancellationToken.None, TradeDate);

        Assert.Equal(RunOutcome.Success, summary.Outcome);
        Assert.Contains("CrawlCnyes", callOrder);
        Assert.Contains("鉅亨網址均價比對已依設定略過", summary.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task 收盤流程不讀取客戶現價_DDE或進場價異常不可能影響收盤流程()
    {
        // Excel 假物件在 ReadHoldingsAsync 直接擲例外：收盤流程若讀取客戶頁籤現價就會失敗。
        var maByCode = new Dictionary<string, MovingAverageResult>(StringComparer.OrdinalIgnoreCase)
        {
            ["2330"] = FullHistoryMovingAverage("2330", close: 900m, ma5: 890m, ma20: 880m, ma60: 870m, ma120: 860m)
        };
        var (service, excel, _, _, repository, _, _) = CreateService(maByCode);

        var summary = await service.RunAsync(isManualRun: true, CancellationToken.None, TradeDate);

        Assert.Equal(RunOutcome.Success, summary.Outcome);
        Assert.Equal(0, excel.ReadCallCount);
        Assert.Equal(0, repository.SaveHoldingsAndAlertsCallCount);
        Assert.Equal(0, summary.HoldingCount);
        Assert.Empty(summary.Alerts);
    }

    [Fact]
    public async Task 收盤流程不產生盤中策略通知_JobRuns紀錄持股與通知數為零()
    {
        var maByCode = new Dictionary<string, MovingAverageResult>(StringComparer.OrdinalIgnoreCase)
        {
            ["2330"] = FullHistoryMovingAverage("2330", close: 900m, ma5: 890m, ma20: 880m, ma60: 870m, ma120: 860m)
        };
        var (service, _, _, _, repository, _, _) = CreateService(maByCode);

        var summary = await service.RunAsync(isManualRun: true, CancellationToken.None, TradeDate);

        Assert.Equal(JobStatus.Succeeded, summary.Status);
        Assert.Equal(0, summary.AlertCount);
        Assert.Empty(repository.SavedAlerts);
        Assert.Contains("BaselineTradeDate=2026-07-13", summary.Message, StringComparison.Ordinal);
        Assert.Contains("下一個交易日盤中才開始使用", summary.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task 官方來源全部休市時_記錄休市且不寫入Excel不更新均價()
    {
        var maByCode = new Dictionary<string, MovingAverageResult>(StringComparer.OrdinalIgnoreCase);
        var (service, excel, marketPrice, movingAverage, _, _, _) = CreateService(maByCode, allHoliday: true);

        var summary = await service.RunAsync(isManualRun: true, CancellationToken.None, TradeDate);

        Assert.Equal(JobStatus.NoTradingData, summary.Status);
        Assert.True(marketPrice.FetchDailyCallCount >= 1);
        Assert.Equal(0, movingAverage.CallCount);
        Assert.Equal(0, excel.WriteCallCount);
    }

    [Fact]
    public async Task 收盤更新執行期間_盤中判斷會被執行鎖擋下並略過()
    {
        var maByCode = new Dictionary<string, MovingAverageResult>(StringComparer.OrdinalIgnoreCase)
        {
            ["2330"] = FullHistoryMovingAverage("2330", close: 900m, ma5: 890m, ma20: 880m, ma60: 870m, ma120: 860m)
        };
        var gate = new WorkflowExecutionGate();
        var (service, _, _, _, _, _, _) = CreateService(maByCode, gate: gate);

        // 模擬收盤更新已持有鎖時，盤中 Tick 的 TryEnter 必須失敗（直接略過，不排隊）。
        using (await gate.EnterAsync("收盤更新", CancellationToken.None))
        {
            Assert.Null(gate.TryEnter("盤中判斷"));
            Assert.Equal("收盤更新", gate.CurrentOwner);
        }

        // 收盤更新結束後，鎖釋放，盤中判斷可正常進入。
        using var ticket = gate.TryEnter("盤中判斷");
        Assert.NotNull(ticket);

        // 而收盤流程本身在鎖可用時照常成功。
        var summary = await service.RunAsync(isManualRun: true, CancellationToken.None, TradeDate);
        Assert.Equal(RunOutcome.Success, summary.Outcome);
    }

    private static MovingAverageResult FullHistoryMovingAverage(string code, decimal close, decimal ma5, decimal ma20, decimal ma60, decimal ma120) => new(
        code, TradeDate, close, ma5, ma20, ma60, ma120, 120, CalculationStatus.Ok, TradeDate);

    private (ClosePrecomputeJobService Service,
        ThrowOnReadExcelWorkbookService Excel,
        FakeMarketPriceService MarketPrice,
        FakeMovingAverageService MovingAverage,
        FakeYiHeLeeRepository Repository,
        FakeMarketDataRepository MarketDataRepository,
        List<string> CallOrder) CreateService(
        Dictionary<string, MovingAverageResult> movingAveragesByCode,
        bool allHoliday = false,
        WorkflowExecutionGate? gate = null,
        Action<AppSettings>? configureSettings = null)
    {
        var callOrder = new List<string>();
        var settings = AppSettings.CreateDefault();
        settings.WorkbookPath = _workbookPath;
        configureSettings?.Invoke(settings);

        var settingsStore = new FakeSettingsStore(settings);
        var logger = new FakeLogger();
        var marketDataRepository = new FakeMarketDataRepository(movingAveragesByCode.Keys.ToArray());
        var yiHeLeeRepository = new FakeYiHeLeeRepository();
        var excelService = new ThrowOnReadExcelWorkbookService(callOrder);
        var userInteraction = new FakeUserInteraction();
        var marketPriceService = new FakeMarketPriceService(callOrder, allHoliday);
        var movingAverageService = new FakeMovingAverageService(movingAveragesByCode, callOrder);
        var crawlerRegistry = new FakeCrawlerRegistry(new FakeSourceCrawler(callOrder));

        var service = new ClosePrecomputeJobService(
            new FakeClock(TradeDate),
            settingsStore,
            crawlerRegistry,
            yiHeLeeRepository,
            marketDataRepository,
            excelService,
            userInteraction,
            logger,
            new DailyMarketDataJob(marketPriceService),
            marketPriceService,
            movingAverageService,
            new SettingsValidationService(),
            gate ?? new WorkflowExecutionGate());

        return (service, excelService, marketPriceService, movingAverageService, yiHeLeeRepository, marketDataRepository, callOrder);
    }

    private sealed class FakeClock(DateOnly today) : IClock
    {
        public DateTimeOffset GetTaipeiNow() => new(today.ToDateTime(new TimeOnly(13, 35)), TimeSpan.FromHours(8));
        public DateOnly GetTaipeiToday() => today;
    }

    private sealed class FakeLogger : IAppLogger
    {
        public void Info(string message) { }
        public void Warning(string message) { }
        public void Error(string message, Exception? exception = null) { }
    }

    private sealed class FakeSettingsStore(AppSettings settings) : ISettingsStore
    {
        public Task<AppSettings> LoadAsync(CancellationToken cancellationToken = default) => Task.FromResult(settings);
        public Task SaveAsync(AppSettings settings, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

    private sealed class FakeUserInteraction : IUserInteraction
    {
        public Task<bool> ConfirmExcelSafetyAsync(CancellationToken cancellationToken) => Task.FromResult(true);
        public void ShowStatus(string message, int percentComplete = 0) { }
        public void ShowProgressDetail(string message) { }
        public void ShowSuccess(JobRunSummary summary) { }
        public void ShowFailure(JobRunSummary summary) { }
    }

    private sealed class FakeCrawlerRegistry(ISourceCrawler crawler) : ICrawlerRegistry
    {
        public ISourceCrawler Resolve(string providerKey) => crawler;
    }

    private sealed class FakeSourceCrawler(List<string> callOrder) : ISourceCrawler
    {
        public string ProviderKey => "CnyesTechnicalAlignment";

        public Task<CrawlBatch> CrawlAsync(SourceDefinition source, MarketType marketType, DateOnly targetDate, AppSettings settings, CancellationToken cancellationToken)
        {
            if (!callOrder.Contains("CrawlCnyes"))
            {
                callOrder.Add("CrawlCnyes");
            }

            return Task.FromResult(new CrawlBatch(source, marketType, targetDate, targetDate, [], DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, true));
        }
    }

    private sealed class FakeMarketPriceService(List<string> callOrder, bool allHoliday = false) : IMarketPriceService
    {
        public int FetchDailyCallCount { get; private set; }
        public int BackfillCallCount { get; private set; }

        public Task<IReadOnlyList<OfficialPriceBatchSummary>> FetchAndSaveDailyPricesAsync(
            DateOnly targetDate, OfficialMarketDataSettings settings, CancellationToken cancellationToken)
        {
            FetchDailyCallCount++;
            callOrder.Add("FetchDailyPrices");
            var status = allHoliday ? OfficialPriceBatchStatus.Holiday : OfficialPriceBatchStatus.Succeeded;
            IReadOnlyList<OfficialPriceBatchSummary> result =
            [
                CreateSummary(OfficialPriceJobType.DailyMarketData, targetDate, "TWSE", MarketType.Listed, status),
                CreateSummary(OfficialPriceJobType.DailyMarketData, targetDate, "TPEx", MarketType.Otc, status)
            ];
            return Task.FromResult(result);
        }

        public Task<IReadOnlyList<OfficialPriceBatchSummary>> BackfillHistoryAsync(
            DateOnly targetDate,
            OfficialMarketDataSettings settings,
            CancellationToken cancellationToken,
            Action<string>? reportProgress = null,
            IReadOnlyCollection<string>? emergingStockCodes = null,
            IReadOnlyCollection<string>? listedStockCodes = null,
            IReadOnlyCollection<string>? otcStockCodes = null)
        {
            BackfillCallCount++;
            callOrder.Add("BackfillHistory");
            return Task.FromResult<IReadOnlyList<OfficialPriceBatchSummary>>([]);
        }

        public Task<OfficialPriceBatchSummary> FetchAndSaveSingleAsync(
            OfficialPriceJobType jobType, DateOnly targetDate, MarketType marketType, OfficialMarketDataSettings settings, CancellationToken cancellationToken)
            => Task.FromResult(CreateSummary(jobType, targetDate, "TPEx興櫃", marketType,
                allHoliday ? OfficialPriceBatchStatus.Holiday : OfficialPriceBatchStatus.Succeeded));

        private static OfficialPriceBatchSummary CreateSummary(
            OfficialPriceJobType jobType, DateOnly targetDate, string provider, MarketType marketType, OfficialPriceBatchStatus status)
            => new(
                Guid.NewGuid().ToString("D"), jobType, targetDate, provider, marketType, targetDate,
                DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, 0, 0, 0, 0, 0, 0,
                status, null);
    }

    private sealed class FakeMovingAverageService(Dictionary<string, MovingAverageResult> resultsByCode, List<string> callOrder) : IMovingAverageService
    {
        public int CallCount { get; private set; }

        public Task<MovingAverageResult> CalculateAsync(string stockCode, DateOnly tradeDate, CancellationToken cancellationToken)
            => Task.FromResult(resultsByCode.TryGetValue(stockCode, out var result)
                ? result
                : new MovingAverageResult(stockCode, tradeDate, null, null, null, null, null, 0, CalculationStatus.InsufficientHistory));

        public Task<IReadOnlyList<MovingAverageResult>> CalculateManyAsync(
            IReadOnlyCollection<string> stockCodes, DateOnly tradeDate, CancellationToken cancellationToken)
        {
            CallCount++;
            callOrder.Add("CalculateMovingAverages");
            IReadOnlyList<MovingAverageResult> list = stockCodes
                .Where(resultsByCode.ContainsKey)
                .Select(code => resultsByCode[code])
                .ToArray();
            return Task.FromResult(list);
        }
    }

    /// <summary>收盤流程禁止讀取客戶頁籤：本假物件在 ReadHoldingsAsync 直接擲例外以驗證流程隔離。</summary>
    private sealed class ThrowOnReadExcelWorkbookService(List<string> callOrder) : IExcelWorkbookService
    {
        public int ReadCallCount { get; private set; }
        public int WriteCallCount { get; private set; }
        public IReadOnlyList<DailyMovingAverageSnapshot>? WrittenResults { get; private set; }

        public Task<IReadOnlyList<CustomerHolding>> ReadHoldingsAsync(
            AppSettings settings, DateOnly targetDate, CancellationToken cancellationToken, Action<string>? reportProgress = null)
        {
            ReadCallCount++;
            throw new InvalidOperationException("收盤流程不得讀取客戶頁籤現價（含 DDE 欄位）。");
        }

        public Task WriteStrategyResultsAsync(
            AppSettings settings, DateOnly targetDate, IReadOnlyList<DailyMovingAverageSnapshot> rows, CancellationToken cancellationToken)
        {
            WriteCallCount++;
            callOrder.Add("WriteResults");
            WrittenResults = rows;
            return Task.CompletedTask;
        }
    }

    private sealed class FakeYiHeLeeRepository : IYiHeLeeRepository
    {
        public List<StrategyAlert> SavedAlerts { get; } = [];
        public int SaveHoldingsAndAlertsCallCount { get; private set; }

        public Task InitializeAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task<Guid> BeginJobAsync(DateOnly targetDate, int attemptNumber, DateTimeOffset startedAt, CancellationToken cancellationToken)
            => Task.FromResult(Guid.NewGuid());

        public Task RecordJobDetailAsync(Guid jobId, CrawlBatch batch, string status, string? errorMessage, CancellationToken cancellationToken)
            => Task.CompletedTask;

        public Task RecordJobDetailFailureAsync(Guid jobId, SourceDefinition source, MarketType marketType, DateOnly targetDate, string status, string errorMessage, DateTimeOffset startedAt, DateTimeOffset completedAt, CancellationToken cancellationToken)
            => Task.CompletedTask;

        public Task SaveCompleteTechnicalBatchAsync(Guid jobId, IReadOnlyList<CrawlBatch> batches, CancellationToken cancellationToken)
            => Task.CompletedTask;

        public Task<IReadOnlyList<TechnicalIndicator>> GetTechnicalIndicatorsAsync(DateOnly tradeDate, CancellationToken cancellationToken)
            => Task.FromResult<IReadOnlyList<TechnicalIndicator>>([]);

        public Task SaveHoldingsAndAlertsAsync(
            Guid jobId, DateOnly tradeDate, string workbookPath, IReadOnlyList<CustomerHolding> holdings, IReadOnlyList<StrategyAlert> alerts, CancellationToken cancellationToken)
        {
            SaveHoldingsAndAlertsCallCount++;
            SavedAlerts.AddRange(alerts);
            return Task.CompletedTask;
        }

        public Task CompleteJobAsync(Guid jobId, JobRunSummary summary, CancellationToken cancellationToken) => Task.CompletedTask;

        public Task<int> GetAttemptCountAsync(DateOnly targetDate, CancellationToken cancellationToken) => Task.FromResult(0);

        public Task<JobRunSummary?> GetLatestJobSummaryAsync(CancellationToken cancellationToken) => Task.FromResult<JobRunSummary?>(null);

        public Task<JobRunSummary?> GetLatestJobSummaryForDateAsync(DateOnly targetDate, CancellationToken cancellationToken) => Task.FromResult<JobRunSummary?>(null);
    }

    private sealed class FakeMarketDataRepository(IReadOnlyList<string> stockCodesWithDailyPrice) : IMarketDataRepository
    {
        public List<MovingAverageResult> SavedMovingAverages { get; } = [];

        public Task InitializeAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task<string> BeginPriceBatchAsync(OfficialPriceJobType jobType, DateOnly targetDate, string sourceProvider, MarketType marketType, DateTimeOffset startedAt, CancellationToken cancellationToken)
            => Task.FromResult(Guid.NewGuid().ToString("D"));

        public Task CompletePriceBatchAsync(OfficialPriceBatchSummary summary, CancellationToken cancellationToken) => Task.CompletedTask;

        public Task<(int Inserted, int Updated)> UpsertDailyPricesAsync(IReadOnlyList<OfficialStockPrice> prices, CancellationToken cancellationToken)
            => Task.FromResult((prices.Count, 0));

        public Task<IReadOnlyList<(DateOnly TradeDate, decimal ClosePrice)>> GetRecentClosePricesAsync(string stockCode, DateOnly upToDate, int maxTradingDays, CancellationToken cancellationToken)
            => Task.FromResult<IReadOnlyList<(DateOnly, decimal)>>([]);

        public Task<MarketType?> GetStockMarketTypeAsync(string stockCode, CancellationToken cancellationToken)
            => Task.FromResult<MarketType?>(MarketType.Listed);

        public Task<IReadOnlyDictionary<string, MarketType>> GetStockMarketTypesAsync(IReadOnlyCollection<string> stockCodes, CancellationToken cancellationToken)
            => Task.FromResult<IReadOnlyDictionary<string, MarketType>>(
                stockCodes.ToDictionary(code => code, _ => MarketType.Listed, StringComparer.OrdinalIgnoreCase));

        public Task<IReadOnlyList<string>> GetStockCodesWithDailyPriceAsync(DateOnly tradeDate, CancellationToken cancellationToken)
            => Task.FromResult(stockCodesWithDailyPrice);

        public Task SaveMovingAverageResultsAsync(DateOnly tradeDate, IReadOnlyList<MovingAverageResult> results, CancellationToken cancellationToken)
        {
            SavedMovingAverages.AddRange(results);
            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<MovingAverageResult>> GetMovingAverageResultsAsync(DateOnly tradeDate, CancellationToken cancellationToken)
            => Task.FromResult<IReadOnlyList<MovingAverageResult>>(SavedMovingAverages);

        public Task<IReadOnlyList<DailyMovingAverageSnapshot>> GetMovingAverageSnapshotsAsync(DateOnly tradeDate, CancellationToken cancellationToken)
            => Task.FromResult<IReadOnlyList<DailyMovingAverageSnapshot>>(
                SavedMovingAverages
                    .Select(x => new DailyMovingAverageSnapshot(
                        x.TradeDate, x.StockCode, x.StockCode, x.ClosePrice,
                        x.MovingAverage5, x.MovingAverage20, x.MovingAverage60, x.MovingAverage120,
                        x.CalculationStatus, x.MissingReason))
                    .ToArray());

        public Task<IReadOnlyList<DailyMovingAverageSnapshot>> GetMovingAverageAnomaliesAsync(DateOnly tradeDate, CancellationToken cancellationToken)
            => Task.FromResult<IReadOnlyList<DailyMovingAverageSnapshot>>([]);

        public Task<int> GetDistinctTradeDateCountAsync(DateOnly upToDate, int maxTradingDays, CancellationToken cancellationToken)
            => Task.FromResult(maxTradingDays);

        public Task<int> GetDistinctTradeDateCountAsync(DateOnly upToDate, int maxTradingDays, MarketType marketType, CancellationToken cancellationToken)
            => Task.FromResult(maxTradingDays);

        public Task<int> GetDistinctTradeDateCountAsync(DateOnly upToDate, int maxTradingDays, string stockCode, CancellationToken cancellationToken)
            => Task.FromResult(maxTradingDays);

        public Task<bool> HasDailyPricesAsync(DateOnly tradeDate, MarketType marketType, CancellationToken cancellationToken)
            => Task.FromResult(true);

        public Task<bool> HasDailyPriceAsync(DateOnly tradeDate, string stockCode, CancellationToken cancellationToken)
            => Task.FromResult(true);

        public Task<bool> HasSucceededBatchAsync(OfficialPriceJobType jobType, DateOnly targetDate, string sourceProvider, CancellationToken cancellationToken)
            => Task.FromResult(false);

        public Task<bool> HasResolvedHolidayBatchAsync(DateOnly targetDate, string sourceProvider, CancellationToken cancellationToken)
            => Task.FromResult(false);

        public Task<StockDailyPriceQueryResult> QueryDailyPricesAsync(StockDailyPriceQueryFilter filter, CancellationToken cancellationToken)
            => Task.FromResult(new StockDailyPriceQueryResult([], 0, filter.Page, filter.PageSize));

        public Task<DateOnly?> GetLatestTradeDateAsync(CancellationToken cancellationToken) => Task.FromResult<DateOnly?>(null);

        public Task<IReadOnlySet<string>> GetConfirmedNoEmergingDataCodesAsync(DateOnly tradeDate, IReadOnlyCollection<string> stockCodes, CancellationToken cancellationToken)
            => Task.FromResult<IReadOnlySet<string>>(new HashSet<string>(StringComparer.OrdinalIgnoreCase));

        public Task RecordConfirmedNoEmergingDataAsync(DateOnly tradeDate, IReadOnlyCollection<string> stockCodes, DateTimeOffset checkedAt, CancellationToken cancellationToken)
            => Task.CompletedTask;
    }
}
