using Microsoft.Data.Sqlite;
using YiHeLee.Application.Abstractions;
using YiHeLee.Application.Services;
using YiHeLee.Domain;
using YiHeLee.Infrastructure.Data;

namespace YiHeLee.Tests;

/// <summary>
/// 使用實際 SQLite 驗證盤中基準準備：第一次空庫會回補、計算均價並同次判斷；
/// 第二次與模擬重啟後直接沿用已保存基準，不再呼叫官方價格服務。
/// </summary>
public sealed class IntradayMonitoringSqliteBaselineTests : IDisposable
{
    private static readonly DateOnly EvaluationDate = new(2026, 7, 13);
    private static readonly DateOnly BaselineDate = new(2026, 7, 10);
    private const string WorkbookPath = @"C:\Data\客戶.xlsx";

    private readonly string _databasePath = Path.Combine(Path.GetTempPath(), $"yihelee-intraday-baseline-{Guid.NewGuid():N}.db");
    private readonly MutableClock _clock = new(new DateTimeOffset(EvaluationDate.ToDateTime(new TimeOnly(10, 0)), TimeSpan.FromHours(8)));

    [Fact]
    public async Task 實際SQLite_第一次缺漏會回補並同次判斷_第二次與重啟後走快速路徑()
    {
        var settings = AppSettings.CreateDefault();
        settings.WorkbookPath = WorkbookPath;
        settings.OfficialMarketData.RequiredTradingDaysForMa120 = 120;
        settings.OfficialMarketData.MaxBackfillLookbackCalendarDays = 220;
        var settingsStore = new FakeSettingsStore(settings);
        var marketDataRepository = CreateMarketDataRepository();
        var intradayStateRepository = CreateIntradayRepository();
        var fakeMarketPrice = new BackfillingMarketPriceService(marketDataRepository, _clock);
        var excel = new FakeExcelWorkbookService();

        var service = CreateService(marketDataRepository, intradayStateRepository, fakeMarketPrice, excel, settingsStore);

        var first = await service.RunOnceAsync(false, _clock.GetTaipeiNow(), CancellationToken.None);

        Assert.Equal(IntradayRunStatus.Succeeded, first.Status);
        Assert.Equal(BaselineDate, first.BaselineTradeDate);
        Assert.Equal(1, fakeMarketPrice.BackfillCallCount);
        Assert.Equal(1, excel.ReadCallCount);
        Assert.Equal(1, first.HoldingCount);
        Assert.Equal(1, first.ActiveTriggerCount);

        var savedMovingAverages = await marketDataRepository.GetMovingAverageResultsAsync(BaselineDate, CancellationToken.None);
        var ma = Assert.Single(savedMovingAverages);
        Assert.Equal(CalculationStatus.Ok, ma.CalculationStatus);
        Assert.NotNull(ma.MovingAverage5);
        Assert.NotNull(ma.MovingAverage20);
        Assert.NotNull(ma.MovingAverage60);
        Assert.NotNull(ma.MovingAverage120);

        var runs = await intradayStateRepository.GetEvaluationRunsAsync(EvaluationDate, 10, CancellationToken.None);
        Assert.Contains(runs, x => x.BaselineTradeDate == BaselineDate && x.HoldingCount == 1);

        _clock.Advance(TimeSpan.FromSeconds(30));
        var second = await service.RunOnceAsync(true, _clock.GetTaipeiNow(), CancellationToken.None);

        Assert.Equal(IntradayRunStatus.Succeeded, second.Status);
        Assert.Equal(1, fakeMarketPrice.BackfillCallCount);
        Assert.Equal(2, excel.ReadCallCount);
        Assert.Contains("本輪只重新讀取客戶價格", second.Message, StringComparison.Ordinal);

        // 模擬程式重啟：重新建立 Repository／Resolver／Service，沿用同一個 SQLite 檔。
        var restartedMarketDataRepository = CreateMarketDataRepository();
        var restartedIntradayStateRepository = CreateIntradayRepository();
        var restartedExcel = new FakeExcelWorkbookService();
        var restartedService = CreateService(
            restartedMarketDataRepository,
            restartedIntradayStateRepository,
            fakeMarketPrice,
            restartedExcel,
            settingsStore);

        _clock.Advance(TimeSpan.FromSeconds(30));
        var afterRestart = await restartedService.RunOnceAsync(false, _clock.GetTaipeiNow(), CancellationToken.None);

        Assert.Equal(IntradayRunStatus.Succeeded, afterRestart.Status);
        Assert.Equal(BaselineDate, afterRestart.BaselineTradeDate);
        Assert.Equal(1, fakeMarketPrice.BackfillCallCount);
        Assert.Equal(1, restartedExcel.ReadCallCount);
    }

    private SqliteMarketDataRepository CreateMarketDataRepository()
    {
        var repository = new SqliteMarketDataRepository(_databasePath, _clock);
        repository.InitializeAsync().GetAwaiter().GetResult();
        return repository;
    }

    private SqliteIntradayStateRepository CreateIntradayRepository()
    {
        var repository = new SqliteIntradayStateRepository(_databasePath, _clock);
        repository.InitializeAsync().GetAwaiter().GetResult();
        return repository;
    }

    private IntradayMonitoringService CreateService(
        SqliteMarketDataRepository marketDataRepository,
        SqliteIntradayStateRepository intradayStateRepository,
        IMarketPriceService marketPriceService,
        IExcelWorkbookService excel,
        ISettingsStore settingsStore)
    {
        var resolver = new TradingDateResolver(marketDataRepository);
        var preparation = new BaselinePreparationService(
            marketDataRepository,
            marketPriceService,
            new MovingAverageService(marketDataRepository),
            resolver,
            _clock,
            new FakeUserInteraction(),
            new FakeLogger());
        return new IntradayMonitoringService(
            _clock,
            settingsStore,
            resolver,
            marketDataRepository,
            excel,
            new StockIdentityResolutionService(marketDataRepository),
            new StrategyEvaluationService(),
            intradayStateRepository,
            new WorkflowExecutionGate(),
            new FakeLogger(),
            preparation);
    }

    private sealed class BackfillingMarketPriceService(SqliteMarketDataRepository repository, IClock clock) : IMarketPriceService
    {
        public int BackfillCallCount { get; private set; }

        public Task<IReadOnlyList<OfficialPriceBatchSummary>> FetchAndSaveDailyPricesAsync(DateOnly targetDate, OfficialMarketDataSettings settings, CancellationToken cancellationToken)
            => throw new InvalidOperationException("盤中基準準備不得呼叫每日正式收盤更新。");

        public async Task<IReadOnlyList<OfficialPriceBatchSummary>> BackfillHistoryAsync(
            DateOnly targetDate,
            OfficialMarketDataSettings settings,
            CancellationToken cancellationToken,
            Action<string>? reportProgress = null,
            IReadOnlyCollection<string>? emergingStockCodes = null,
            IReadOnlyCollection<string>? listedStockCodes = null,
            IReadOnlyCollection<string>? otcStockCodes = null)
        {
            BackfillCallCount++;
            var dates = CreateTradingDatesEndingAt(BaselineDate, 120);
            foreach (var (date, index) in dates.Select((date, index) => (date, index)))
            {
                var price = new OfficialStockPrice(
                    "2330",
                    "台積電",
                    MarketType.Listed,
                    date,
                    100m + index,
                    "TWSE",
                    "https://example.test/twse",
                    date,
                    $"batch-{date:yyyyMMdd}",
                    clock.GetTaipeiNow());
                await repository.UpsertDailyPricesAsync([price], cancellationToken).ConfigureAwait(false);
            }

            return
            [
                new OfficialPriceBatchSummary(
                    "fake-backfill", OfficialPriceJobType.HistoricalBackfill, BaselineDate,
                    "TWSE", MarketType.Listed, BaselineDate,
                    clock.GetTaipeiNow(), clock.GetTaipeiNow(),
                    120, 120, 0, 0, 0, 0,
                    OfficialPriceBatchStatus.Succeeded, null)
            ];
        }

        public Task<OfficialPriceBatchSummary> FetchAndSaveSingleAsync(OfficialPriceJobType jobType, DateOnly targetDate, MarketType marketType, OfficialMarketDataSettings settings, CancellationToken cancellationToken)
            => throw new InvalidOperationException("本測試不應呼叫單一市場官方抓取。");

        private static DateOnly[] CreateTradingDatesEndingAt(DateOnly endDate, int count)
        {
            var dates = new List<DateOnly>(count);
            var cursor = endDate;
            while (dates.Count < count)
            {
                if (cursor.DayOfWeek is not (DayOfWeek.Saturday or DayOfWeek.Sunday))
                {
                    dates.Add(cursor);
                }

                cursor = cursor.AddDays(-1);
            }

            dates.Reverse();
            return dates.ToArray();
        }
    }

    private sealed class FakeExcelWorkbookService : IExcelWorkbookService
    {
        public int ReadCallCount { get; private set; }

        public Task<IReadOnlyList<CustomerHolding>> ReadHoldingsAsync(AppSettings settings, DateOnly targetDate, CancellationToken cancellationToken, Action<string>? reportProgress = null)
        {
            ReadCallCount++;
            IReadOnlyList<CustomerHolding> holdings =
            [
                new(
                    targetDate,
                    WorkbookPath,
                    "客戶頁籤",
                    "測試客戶",
                    4,
                    "2330",
                    "台積電",
                    CurrentPrice: 110m,
                    Quantity: 1,
                    HoldingKey: "2330-4",
                    CurrentPriceIssue: null,
                    EntryAveragePrice: 110m,
                    EntryAveragePriceIssue: null)
            ];
            return Task.FromResult(holdings);
        }

        public Task WriteStrategyResultsAsync(AppSettings settings, DateOnly targetDate, IReadOnlyList<DailyMovingAverageSnapshot> rows, CancellationToken cancellationToken)
            => throw new InvalidOperationException("盤中判斷不得寫入 Excel 均價頁籤。");
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

    private sealed class FakeLogger : IAppLogger
    {
        public void Info(string message) { }
        public void Warning(string message) { }
        public void Error(string message, Exception? exception = null) { }
    }

    private sealed class MutableClock(DateTimeOffset now) : IClock
    {
        private DateTimeOffset _now = now;
        public void Advance(TimeSpan delta) => _now += delta;
        public DateTimeOffset GetTaipeiNow() => _now;
        public DateOnly GetTaipeiToday() => DateOnly.FromDateTime(_now.DateTime);
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try { File.Delete(_databasePath); } catch { }
    }
}
