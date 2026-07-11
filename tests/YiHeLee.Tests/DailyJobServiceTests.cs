using YiHeLee.Application.Abstractions;
using YiHeLee.Application.Services;
using YiHeLee.Domain;

namespace YiHeLee.Tests;

/// <summary>
/// 驗證 DailyJobService 端到端流程符合「DDE 現價異常只能影響最後的均線比較」的最高優先規則：
/// 即使 Excel「現價」欄位的 DDE 連結無效（#N/A、空白、0、負數、無法解析文字等），仍必須完成
/// 官方收盤價擷取、歷史回補、MA5／MA20／MA60／MA120 計算、鉅亨交叉驗證與 Excel 完整輸出，
/// 只有最後的「現價 vs 均線」比較被標記為無法判斷；不得中斷、不得跳過其他持股、不得整批失敗。
/// </summary>
public sealed class DailyJobServiceTests : IDisposable
{
    private static readonly DateOnly TradeDate = new(2026, 7, 9);
    private readonly string _workbookPath = Path.Combine(Path.GetTempPath(), $"yihelee-test-{Guid.NewGuid():N}.xlsx");

    public DailyJobServiceTests() => File.WriteAllBytes(_workbookPath, []);

    public void Dispose()
    {
        if (File.Exists(_workbookPath))
        {
            File.Delete(_workbookPath);
        }
    }

    [Theory]
    [InlineData("#N/A（DDE 尚未取得資料，看盤軟體可能未開啟或未連線）")]
    [InlineData("#NAME?（DDE 函數名稱錯誤）")]
    [InlineData("儲存格為空白")]
    [InlineData("值為 0（可能為未連線或無報價）")]
    [InlineData("值為負數，無法作為現價")]
    [InlineData("文字「查詢中」無法解析為數字")]
    public async Task DDE現價無效各種情況_不影響官方價格回補與均線計算_只有最後比較無法判斷(string issue)
    {
        var holding = CreateHolding("2330", "台積電", currentPrice: null, currentPriceIssue: issue);
        var ma = FullHistoryMovingAverage("2330", close: 900m, ma5: 890m, ma20: 880m, ma60: 870m, ma120: 860m);

        var (service, excel, marketPrice, movingAverage, _, marketDataRepository) = CreateService(
            [holding],
            new Dictionary<string, MovingAverageResult>(StringComparer.OrdinalIgnoreCase) { ["2330"] = ma },
            new Dictionary<string, MarketType>(StringComparer.OrdinalIgnoreCase) { ["2330"] = MarketType.Listed });

        var summary = await service.RunAsync(isManualRun: true, CancellationToken.None, TradeDate);

        Assert.Equal(RunOutcome.Success, summary.Outcome);
        Assert.True(marketPrice.FetchDailyCallCount >= 1, "官方每日收盤價必須照常擷取，不得因 DDE 無效而略過。");
        Assert.True(marketPrice.BackfillCallCount >= 1, "歷史回補必須照常執行。");
        Assert.True(movingAverage.CallCount >= 1, "均線必須照常計算。");
        Assert.Equal(1, excel.WriteCallCount);

        var row = Assert.Single(excel.WrittenResults!);
        Assert.Equal("無效", row.CurrentPriceStatus);
        Assert.Equal(issue, row.CurrentPriceIssue);
        Assert.Equal(900m, row.ClosePrice);
        Assert.Equal(890m, row.MovingAverage5);
        Assert.Equal(880m, row.MovingAverage20);
        Assert.Equal(870m, row.MovingAverage60);
        Assert.Equal(860m, row.MovingAverage120);
        Assert.Equal(120, row.AvailableTradingDayCount);
        Assert.Equal("正常", row.CalculationStatus);
        Assert.Equal("現價無效，暫時無法判斷", row.OverallResult);

        // 資料庫官方收盤價與均線必須照常保存，不得因 CurrentPrice 無效而略過或刪除。
        Assert.Single(marketDataRepository.SavedMovingAverages, x => x.StockCode == "2330" && x.MovingAverage5 == 890m);
    }

    [Fact]
    public async Task 單一持股DDE錯誤不影響其他持股的官方價格回補與均線計算()
    {
        var holdingA = CreateHolding("2330", "台積電", currentPrice: 900m); // DDE 正常
        var holdingB = CreateHolding("5351", "鈺創", currentPrice: null, currentPriceIssue: "#N/A"); // DDE 無效
        var holdingC = CreateHolding("3691", "碩禾", currentPrice: null, currentPriceIssue: "儲存格為空白"); // DDE 無效

        var maByCode = new Dictionary<string, MovingAverageResult>(StringComparer.OrdinalIgnoreCase)
        {
            ["2330"] = FullHistoryMovingAverage("2330", close: 900m, ma5: 890m, ma20: 880m, ma60: 870m, ma120: 860m),
            ["5351"] = FullHistoryMovingAverage("5351", close: 100m, ma5: 95m, ma20: 90m, ma60: 85m, ma120: 80m),
            ["3691"] = FullHistoryMovingAverage("3691", close: 200m, ma5: 195m, ma20: 190m, ma60: 185m, ma120: 180m)
        };
        var marketTypes = new Dictionary<string, MarketType>(StringComparer.OrdinalIgnoreCase)
        {
            ["2330"] = MarketType.Listed,
            ["5351"] = MarketType.Otc,
            ["3691"] = MarketType.Otc
        };

        var (service, excel, marketPrice, movingAverage, _, _) = CreateService([holdingA, holdingB, holdingC], maByCode, marketTypes);

        var summary = await service.RunAsync(isManualRun: true, CancellationToken.None, TradeDate);

        Assert.Equal(RunOutcome.Success, summary.Outcome);
        Assert.Equal(3, excel.WrittenResults!.Count);

        var rowA = Assert.Single(excel.WrittenResults!, x => x.ResolvedStockCode == "2330");
        var rowB = Assert.Single(excel.WrittenResults!, x => x.ResolvedStockCode == "5351");
        var rowC = Assert.Single(excel.WrittenResults!, x => x.ResolvedStockCode == "3691");

        Assert.Equal("正常", rowA.CurrentPriceStatus);
        Assert.NotEqual("現價無效，暫時無法判斷", rowA.OverallResult);
        Assert.Equal(890m, rowA.MovingAverage5);

        Assert.Equal("無效", rowB.CurrentPriceStatus);
        Assert.Equal("現價無效，暫時無法判斷", rowB.OverallResult);
        Assert.Equal(95m, rowB.MovingAverage5); // 均線仍完整輸出，不因 DDE 無效而留白。

        Assert.Equal("無效", rowC.CurrentPriceStatus);
        Assert.Equal("現價無效，暫時無法判斷", rowC.OverallResult);
        Assert.Equal(185m, rowC.MovingAverage60);
    }

    [Fact]
    public async Task 現價正常但未觸發任何均線條件時_不產生中央通知但Excel仍須輸出完整列()
    {
        // 現價低於所有均線，三項條件皆不成立。
        var holding = CreateHolding("2330", "台積電", currentPrice: 50m);
        var ma = FullHistoryMovingAverage("2330", close: 900m, ma5: 890m, ma20: 880m, ma60: 870m, ma120: 860m);

        var (service, excel, _, _, repository, _) = CreateService(
            [holding],
            new Dictionary<string, MovingAverageResult>(StringComparer.OrdinalIgnoreCase) { ["2330"] = ma },
            new Dictionary<string, MarketType>(StringComparer.OrdinalIgnoreCase) { ["2330"] = MarketType.Listed });

        var summary = await service.RunAsync(isManualRun: true, CancellationToken.None, TradeDate);

        Assert.Equal(0, summary.AlertCount);
        Assert.DoesNotContain(repository.SavedAlerts, x => x.AlertKind == AlertKind.MovingAverageTriggered);

        var row = Assert.Single(excel.WrittenResults!);
        Assert.Equal("未觸發", row.OverallResult);
        Assert.False(row.Ma5Match);
        Assert.False(row.Ma20Match);
        Assert.False(row.Ma120Match);
        Assert.Equal(890m, row.MovingAverage5);
    }

    [Fact]
    public async Task DDE無效但均線資料完整時_MA5到MA120皆須顯示_不得標記為均線計算失敗()
    {
        var holding = CreateHolding("2330", "台積電", currentPrice: null, currentPriceIssue: "#N/A");
        var ma = FullHistoryMovingAverage("2330", close: 900m, ma5: 890m, ma20: 880m, ma60: 870m, ma120: 860m);

        var (service, excel, _, _, _, _) = CreateService(
            [holding],
            new Dictionary<string, MovingAverageResult>(StringComparer.OrdinalIgnoreCase) { ["2330"] = ma },
            new Dictionary<string, MarketType>(StringComparer.OrdinalIgnoreCase) { ["2330"] = MarketType.Listed });

        await service.RunAsync(isManualRun: true, CancellationToken.None, TradeDate);

        var row = Assert.Single(excel.WrittenResults!);
        Assert.NotNull(row.MovingAverage5);
        Assert.NotNull(row.MovingAverage20);
        Assert.NotNull(row.MovingAverage60);
        Assert.NotNull(row.MovingAverage120);
        Assert.Equal("正常", row.CalculationStatus);
        Assert.NotEqual("均線計算失敗", row.CalculationStatus);
        Assert.Equal("無效", row.CurrentPriceStatus);
    }

    [Fact]
    public async Task 即使DDE現價無效_讀取持股回補均線鉅亨驗證與Excel寫入仍依序全部執行()
    {
        var holding = CreateHolding("2330", "台積電", currentPrice: null, currentPriceIssue: "#N/A");
        var ma = FullHistoryMovingAverage("2330", close: 900m, ma5: 890m, ma20: 880m, ma60: 870m, ma120: 860m);
        var callOrder = new List<string>();

        var (service, _, _, _, _, _) = CreateService(
            [holding],
            new Dictionary<string, MovingAverageResult>(StringComparer.OrdinalIgnoreCase) { ["2330"] = ma },
            new Dictionary<string, MarketType>(StringComparer.OrdinalIgnoreCase) { ["2330"] = MarketType.Listed },
            callOrder);

        await service.RunAsync(isManualRun: true, CancellationToken.None, TradeDate);

        Assert.Contains("FetchDailyPrices", callOrder);
        Assert.Contains("ReadHoldings", callOrder);
        Assert.Contains("BackfillHistory", callOrder);
        Assert.Contains("CalculateMovingAverages", callOrder);
        Assert.Contains("CrawlCnyes", callOrder);
        Assert.Contains("WriteResults", callOrder);

        // 持股讀取必須在回補、均線計算之前（回補與均線計算依賴持股代碼）；
        // 鉅亨交叉驗證在均線算完之後才進行；Excel 寫入必須是最後一步。
        Assert.True(callOrder.IndexOf("ReadHoldings") < callOrder.IndexOf("BackfillHistory"));
        Assert.True(callOrder.IndexOf("BackfillHistory") < callOrder.IndexOf("CalculateMovingAverages"));
        Assert.True(callOrder.IndexOf("CalculateMovingAverages") < callOrder.IndexOf("CrawlCnyes"));
        Assert.True(callOrder.IndexOf("CrawlCnyes") < callOrder.IndexOf("WriteResults"));
        Assert.Equal(callOrder.Count - 1, callOrder.IndexOf("WriteResults"));
    }

    [Fact]
    public async Task Excel完整結果列數等於有效持股數加診斷列數_不等於alerts數量()
    {
        var triggeredHolding = CreateHolding("2330", "台積電", currentPrice: 900m); // 會觸發
        var notTriggeredHolding = CreateHolding("5351", "鈺創", currentPrice: 10m); // 不會觸發
        var invalidDdeHolding = CreateHolding("3691", "碩禾", currentPrice: null, currentPriceIssue: "#N/A"); // 現價無效
        var unrecognizedHolding = CreateHolding("10037677", "疑似金額", currentPrice: 100m); // 無法識別的代碼（診斷列）

        var maByCode = new Dictionary<string, MovingAverageResult>(StringComparer.OrdinalIgnoreCase)
        {
            ["2330"] = FullHistoryMovingAverage("2330", close: 900m, ma5: 890m, ma20: 880m, ma60: 870m, ma120: 860m),
            ["5351"] = FullHistoryMovingAverage("5351", close: 200m, ma5: 195m, ma20: 190m, ma60: 185m, ma120: 180m),
            ["3691"] = FullHistoryMovingAverage("3691", close: 300m, ma5: 295m, ma20: 290m, ma60: 285m, ma120: 280m)
        };
        var marketTypes = new Dictionary<string, MarketType>(StringComparer.OrdinalIgnoreCase)
        {
            ["2330"] = MarketType.Listed,
            ["5351"] = MarketType.Otc,
            ["3691"] = MarketType.Otc
        };

        var (service, excel, _, _, repository, _) = CreateService(
            [triggeredHolding, notTriggeredHolding, invalidDdeHolding, unrecognizedHolding], maByCode, marketTypes);

        var summary = await service.RunAsync(isManualRun: true, CancellationToken.None, TradeDate);

        Assert.Equal(4, excel.WrittenResults!.Count);
        Assert.Equal(4, summary.HoldingCount);

        // alerts 只涵蓋需要中央通知的子集合（觸發、現價無效、無法識別），未觸發的 5351 不在其中，
        // 因此 alerts 數量必須小於 Excel 完整結果列數，不得把兩者混為一談。
        Assert.True(repository.SavedAlerts.Count < excel.WrittenResults!.Count);
        Assert.DoesNotContain(repository.SavedAlerts, x => x.StockCode == "5351");
        Assert.Contains(excel.WrittenResults!, x => x.RawStockCode == "5351" && x.OverallResult == "未觸發");
        Assert.Contains(excel.WrittenResults!, x => x.RawStockCode == "10037677" && x.CalculationStatus == "股票代碼無法識別");
    }

    private static CustomerHolding CreateHolding(string code, string name, decimal? currentPrice, string? currentPriceIssue = null) => new(
        TradeDate,
        @"C:\Data\客戶.xlsx",
        "客戶頁籤",
        "測試客戶",
        4,
        code,
        name,
        currentPrice,
        10,
        $"key-{code}",
        currentPriceIssue);

    private static MovingAverageResult FullHistoryMovingAverage(string code, decimal close, decimal ma5, decimal ma20, decimal ma60, decimal ma120) => new(
        code, TradeDate, close, ma5, ma20, ma60, ma120, 120, CalculationStatus.Ok, TradeDate);

    private (DailyJobService Service, FakeExcelWorkbookService Excel, FakeMarketPriceService MarketPrice, FakeMovingAverageService MovingAverage, FakeYiHeLeeRepository Repository, FakeMarketDataRepository MarketDataRepository) CreateService(
        IReadOnlyList<CustomerHolding> holdings,
        Dictionary<string, MovingAverageResult> movingAveragesByCode,
        Dictionary<string, MarketType> marketTypesByCode,
        List<string>? callOrder = null)
    {
        callOrder ??= [];
        var settings = AppSettings.CreateDefault();
        settings.WorkbookPath = _workbookPath;

        var settingsStore = new FakeSettingsStore(settings);
        var logger = new FakeLogger();
        var marketDataRepository = new FakeMarketDataRepository(marketTypesByCode);
        var yiHeLeeRepository = new FakeYiHeLeeRepository();
        var excelService = new FakeExcelWorkbookService(holdings, callOrder);
        var userInteraction = new FakeUserInteraction();
        var marketPriceService = new FakeMarketPriceService(callOrder);
        var movingAverageService = new FakeMovingAverageService(movingAveragesByCode, callOrder);
        var crawlerRegistry = new FakeCrawlerRegistry(new FakeSourceCrawler(callOrder));

        var service = new DailyJobService(
            new FakeClock(TradeDate),
            settingsStore,
            crawlerRegistry,
            yiHeLeeRepository,
            marketDataRepository,
            excelService,
            userInteraction,
            logger,
            new DailyMarketDataJob(marketPriceService),
            new HistoricalBackfillJob(marketPriceService),
            marketPriceService,
            movingAverageService,
            new StrategyEvaluationService(),
            new SettingsValidationService(),
            new StockIdentityResolutionService(marketDataRepository));

        return (service, excelService, marketPriceService, movingAverageService, yiHeLeeRepository, marketDataRepository);
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

    private sealed class FakeMarketPriceService(List<string> callOrder) : IMarketPriceService
    {
        public int FetchDailyCallCount { get; private set; }
        public int BackfillCallCount { get; private set; }

        public Task<IReadOnlyList<OfficialPriceBatchSummary>> FetchAndSaveDailyPricesAsync(
            DateOnly targetDate, OfficialMarketDataSettings settings, CancellationToken cancellationToken)
        {
            FetchDailyCallCount++;
            callOrder.Add("FetchDailyPrices");
            IReadOnlyList<OfficialPriceBatchSummary> result =
            [
                CreateSummary(OfficialPriceJobType.DailyMarketData, targetDate, "TWSE", MarketType.Listed),
                CreateSummary(OfficialPriceJobType.DailyMarketData, targetDate, "TPEx", MarketType.Otc)
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
            => Task.FromResult(CreateSummary(jobType, targetDate, "TPEx興櫃", marketType));

        private static OfficialPriceBatchSummary CreateSummary(OfficialPriceJobType jobType, DateOnly targetDate, string provider, MarketType marketType)
            => new(
                Guid.NewGuid().ToString("D"), jobType, targetDate, provider, marketType, targetDate,
                DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, 0, 0, 0, 0, 0, 0,
                OfficialPriceBatchStatus.Succeeded, null);
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

    private sealed class FakeExcelWorkbookService(IReadOnlyList<CustomerHolding> holdings, List<string> callOrder) : IExcelWorkbookService
    {
        public int WriteCallCount { get; private set; }
        public IReadOnlyList<HoldingStrategyResult>? WrittenResults { get; private set; }

        public Task<IReadOnlyList<CustomerHolding>> ReadHoldingsAsync(
            AppSettings settings, DateOnly targetDate, CancellationToken cancellationToken, Action<string>? reportProgress = null)
        {
            callOrder.Add("ReadHoldings");
            return Task.FromResult(holdings);
        }

        public Task WriteStrategyResultsAsync(
            AppSettings settings, DateOnly targetDate, IReadOnlyList<HoldingStrategyResult> results, CancellationToken cancellationToken)
        {
            WriteCallCount++;
            callOrder.Add("WriteResults");
            WrittenResults = results;
            return Task.CompletedTask;
        }
    }

    private sealed class FakeYiHeLeeRepository : IYiHeLeeRepository
    {
        public List<StrategyAlert> SavedAlerts { get; private set; } = [];
        public List<CustomerHolding> SavedHoldings { get; private set; } = [];

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
            SavedHoldings = holdings.ToList();
            SavedAlerts = alerts.ToList();
            return Task.CompletedTask;
        }

        public Task CompleteJobAsync(Guid jobId, JobRunSummary summary, CancellationToken cancellationToken) => Task.CompletedTask;

        public Task<int> GetAttemptCountAsync(DateOnly targetDate, CancellationToken cancellationToken) => Task.FromResult(0);

        public Task<JobRunSummary?> GetLatestJobSummaryAsync(CancellationToken cancellationToken) => Task.FromResult<JobRunSummary?>(null);

        public Task<JobRunSummary?> GetLatestJobSummaryForDateAsync(DateOnly targetDate, CancellationToken cancellationToken) => Task.FromResult<JobRunSummary?>(null);
    }

    /// <summary>供 DailyJobService 直接注入的 IMarketDataRepository 假物件，同時供 StockIdentityResolutionService 共用。</summary>
    private sealed class FakeMarketDataRepository(Dictionary<string, MarketType> marketTypesByCode) : IMarketDataRepository
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
            => Task.FromResult(marketTypesByCode.TryGetValue(stockCode, out var mt) ? mt : (MarketType?)null);

        public Task<IReadOnlyDictionary<string, MarketType>> GetStockMarketTypesAsync(IReadOnlyCollection<string> stockCodes, CancellationToken cancellationToken)
            => Task.FromResult<IReadOnlyDictionary<string, MarketType>>(
                stockCodes.Where(marketTypesByCode.ContainsKey)
                    .ToDictionary(code => code, code => marketTypesByCode[code], StringComparer.OrdinalIgnoreCase));

        public Task SaveMovingAverageResultsAsync(DateOnly tradeDate, IReadOnlyList<MovingAverageResult> results, CancellationToken cancellationToken)
        {
            SavedMovingAverages.AddRange(results);
            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<MovingAverageResult>> GetMovingAverageResultsAsync(DateOnly tradeDate, CancellationToken cancellationToken)
            => Task.FromResult<IReadOnlyList<MovingAverageResult>>(SavedMovingAverages);

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

        public Task<StockDailyPriceQueryResult> QueryDailyPricesAsync(StockDailyPriceQueryFilter filter, CancellationToken cancellationToken)
            => Task.FromResult(new StockDailyPriceQueryResult([], 0, filter.Page, filter.PageSize));

        public Task<DateOnly?> GetLatestTradeDateAsync(CancellationToken cancellationToken) => Task.FromResult<DateOnly?>(null);
    }
}
