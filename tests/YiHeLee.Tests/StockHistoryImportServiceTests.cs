using Microsoft.Data.Sqlite;
using YiHeLee.Application.Abstractions;
using YiHeLee.Application.Services;
using YiHeLee.Domain;
using YiHeLee.Infrastructure.Data;

namespace YiHeLee.Tests;

public sealed class StockHistoryImportServiceTests : IDisposable
{
    // 2026-07-09 為週四；服務以「今天－1」為回溯基準日，與既有 HistoricalBackfillJob 慣例一致。
    private static readonly DateOnly Today = new(2026, 7, 9);
    private readonly string _databasePath;
    private readonly SqliteStockPriceImportRepository _repository;

    public StockHistoryImportServiceTests()
    {
        _databasePath = Path.Combine(Path.GetTempPath(), $"yihelee-import-test-{Guid.NewGuid():N}.db");
        _repository = new SqliteStockPriceImportRepository(_databasePath, new FakeClock(Today));
        _repository.InitializeAsync().GetAwaiter().GetResult();
    }

    [Fact]
    public async Task 建立批次時工作數為交易日數乘以市場數且候選日期排除週六週日()
    {
        var marketPriceService = new FakeMarketPriceService((_, date, market, _) => Task.FromResult(Success(date, market)));
        var service = new StockHistoryImportService(marketPriceService, _repository, new FakeClock(Today), new FakeLogger());
        var options = new StockHistoryImportOptions { DefaultTradingDays = 5 };

        var jobId = await service.CreateJobAsync(new StockHistoryImportRequest(MarketScope.All, 5), options, CancellationToken.None);

        var tasks = await _repository.GetTaskProgressAsync(jobId, CancellationToken.None);
        Assert.Equal(10, tasks.Count); // 5 個有效交易日 × 2 個市場
        Assert.All(tasks, t => Assert.True(t.RequestedDate.DayOfWeek is not (DayOfWeek.Saturday or DayOfWeek.Sunday)));
        Assert.All(tasks, t => Assert.True(t.RequestedDate < Today)); // 歷史回補不含當日，當日由每日排程負責
        Assert.Equal(5, tasks.Select(t => t.RequestedDate).Distinct().Count());
    }

    [Fact]
    public async Task 同時指定開始與結束日期時依日期區間建立工作而非交易日數()
    {
        // 2026-06-29（週一）至 2026-07-03（週五）共 5 個交易日，中間跨過的週末不計入。
        var marketPriceService = new FakeMarketPriceService((_, date, market, _) => Task.FromResult(Success(date, market)));
        var service = new StockHistoryImportService(marketPriceService, _repository, new FakeClock(Today), new FakeLogger());
        var options = new StockHistoryImportOptions();

        var request = new StockHistoryImportRequest(MarketScope.All, 999, new DateOnly(2026, 6, 29), new DateOnly(2026, 7, 3));
        var jobId = await service.CreateJobAsync(request, options, CancellationToken.None);

        var tasks = await _repository.GetTaskProgressAsync(jobId, CancellationToken.None);
        Assert.Equal(10, tasks.Count); // 5 個有效交易日 × 2 個市場
        Assert.All(tasks, t => Assert.True(t.RequestedDate.DayOfWeek is not (DayOfWeek.Saturday or DayOfWeek.Sunday)));
        Assert.Equal(5, tasks.Select(t => t.RequestedDate).Distinct().Count());
        Assert.Equal(new DateOnly(2026, 6, 29), tasks.Min(t => t.RequestedDate));
        Assert.Equal(new DateOnly(2026, 7, 3), tasks.Max(t => t.RequestedDate));
    }

    [Fact]
    public async Task 市場範圍為上市時只建立上市市場的工作()
    {
        var marketPriceService = new FakeMarketPriceService((_, date, market, _) => Task.FromResult(Success(date, market)));
        var service = new StockHistoryImportService(marketPriceService, _repository, new FakeClock(Today), new FakeLogger());
        var options = new StockHistoryImportOptions();

        var jobId = await service.CreateJobAsync(new StockHistoryImportRequest(MarketScope.Listed, 5), options, CancellationToken.None);

        var tasks = await _repository.GetTaskProgressAsync(jobId, CancellationToken.None);
        Assert.Equal(5, tasks.Count);
        Assert.All(tasks, t => Assert.Equal(MarketType.Listed, t.MarketType));
    }

    [Fact]
    public async Task 並行數不超過設定上限()
    {
        var currentConcurrency = 0;
        var maxObservedConcurrency = 0;
        var gate = new object();

        var marketPriceService = new FakeMarketPriceService(async (_, date, market, ct) =>
        {
            lock (gate)
            {
                currentConcurrency++;
                maxObservedConcurrency = Math.Max(maxObservedConcurrency, currentConcurrency);
            }

            await Task.Delay(50, ct);

            lock (gate)
            {
                currentConcurrency--;
            }

            return Success(date, market);
        });

        var service = new StockHistoryImportService(marketPriceService, _repository, new FakeClock(Today), new FakeLogger());
        var options = new StockHistoryImportOptions { MaxConcurrency = 2 };

        var jobId = await service.CreateJobAsync(new StockHistoryImportRequest(MarketScope.All, 5), options, CancellationToken.None);
        await service.RunJobAsync(jobId, new OfficialMarketDataSettings(), options, CancellationToken.None);

        Assert.True(maxObservedConcurrency <= 2, $"觀察到的最大並行數 {maxObservedConcurrency} 超過設定上限 2。");
        Assert.True(maxObservedConcurrency >= 2, "並行數應確實達到設定上限，否則代表未有效並行。");
    }

    [Fact]
    public async Task 暫時性錯誤依指數退避重試至成功且記錄重試次數()
    {
        var attempts = 0;
        var marketPriceService = new FakeMarketPriceService((_, date, market, _) =>
        {
            attempts++;
            if (attempts < 3)
            {
                throw new Application.Exceptions.RetryableJobException("TWSE HTTP 回應失敗：503 服務暫時不可用");
            }

            return Task.FromResult(Success(date, market));
        });

        var service = new StockHistoryImportService(marketPriceService, _repository, new FakeClock(Today), new FakeLogger());
        var options = new StockHistoryImportOptions { MaxConcurrency = 1, RetryBaseDelaySeconds = 0, MaxRetryCount = 3 };

        var jobId = await service.CreateJobAsync(new StockHistoryImportRequest(MarketScope.Listed, 1), options, CancellationToken.None);
        await service.RunJobAsync(jobId, new OfficialMarketDataSettings(), options, CancellationToken.None);

        var tasks = await _repository.GetTaskProgressAsync(jobId, CancellationToken.None);
        var task = Assert.Single(tasks);
        Assert.Equal(StockPriceImportTaskStatus.Succeeded, task.Status);
        Assert.Equal(2, task.RetryCount); // 第1次失敗、第2次失敗、第3次成功 -> 重試2次
    }

    [Fact]
    public async Task 非暫時性錯誤不重試直接標記失敗()
    {
        var attempts = 0;
        var marketPriceService = new FakeMarketPriceService((_, _, _, _) =>
        {
            attempts++;
            throw new Application.Exceptions.RetryableJobException("TWSE 回應缺少 tables 陣列，網站結構可能已變更。");
        });

        var service = new StockHistoryImportService(marketPriceService, _repository, new FakeClock(Today), new FakeLogger());
        var options = new StockHistoryImportOptions { MaxConcurrency = 1, RetryBaseDelaySeconds = 0, MaxRetryCount = 3 };

        var jobId = await service.CreateJobAsync(new StockHistoryImportRequest(MarketScope.Listed, 1), options, CancellationToken.None);
        await service.RunJobAsync(jobId, new OfficialMarketDataSettings(), options, CancellationToken.None);

        Assert.Equal(1, attempts); // 非暫時性錯誤不得重試
        var tasks = await _repository.GetTaskProgressAsync(jobId, CancellationToken.None);
        Assert.Equal(StockPriceImportTaskStatus.Failed, Assert.Single(tasks).Status);
    }

    [Fact]
    public async Task IMarketPriceService以Failed摘要而非例外回報暫時性錯誤時仍會依指數退避重試()
    {
        // 對應實機串接 TWSE／TPEx 官方端點時發現的實際行為：IMarketPriceService 對 HTTP／解析失敗一律內部
        // 捕捉並轉為 Failed 摘要回傳（不拋例外），若只依賴例外攔截來決定是否重試，暫時性錯誤永遠不會被重試。
        var attempts = 0;
        var marketPriceService = new FakeMarketPriceService((jobType, date, market, _) =>
        {
            attempts++;
            if (attempts < 3)
            {
                return Task.FromResult(Failed(jobType, date, market, "TWSE HTTP 回應失敗：503 服務暫時不可用"));
            }

            return Task.FromResult(Success(date, market));
        });

        var service = new StockHistoryImportService(marketPriceService, _repository, new FakeClock(Today), new FakeLogger());
        var options = new StockHistoryImportOptions { MaxConcurrency = 1, RetryBaseDelaySeconds = 0, MaxRetryCount = 3 };

        var jobId = await service.CreateJobAsync(new StockHistoryImportRequest(MarketScope.Listed, 1), options, CancellationToken.None);
        await service.RunJobAsync(jobId, new OfficialMarketDataSettings(), options, CancellationToken.None);

        Assert.Equal(3, attempts);
        var task = Assert.Single(await _repository.GetTaskProgressAsync(jobId, CancellationToken.None));
        Assert.Equal(StockPriceImportTaskStatus.Succeeded, task.Status);
        Assert.Equal(2, task.RetryCount);
    }

    [Fact]
    public async Task IMarketPriceService持續回報NotPublished且重試耗盡時最終狀態為等待來源更新而非失敗()
    {
        var marketPriceService = new FakeMarketPriceService((jobType, date, market, _) =>
            Task.FromResult(NotPublished(jobType, date, market)));

        var service = new StockHistoryImportService(marketPriceService, _repository, new FakeClock(Today), new FakeLogger());
        var options = new StockHistoryImportOptions { MaxConcurrency = 1, RetryBaseDelaySeconds = 0, MaxRetryCount = 2 };

        var jobId = await service.CreateJobAsync(new StockHistoryImportRequest(MarketScope.Listed, 1), options, CancellationToken.None);
        await service.RunJobAsync(jobId, new OfficialMarketDataSettings(), options, CancellationToken.None);

        var task = Assert.Single(await _repository.GetTaskProgressAsync(jobId, CancellationToken.None));
        Assert.Equal(StockPriceImportTaskStatus.WaitingForSource, task.Status);
        Assert.Equal(2, task.RetryCount);
    }

    [Fact]
    public async Task 部分工作失敗時整批狀態為部分失敗而非全部卡在執行中()
    {
        var marketPriceService = new FakeMarketPriceService((_, date, market, _) =>
        {
            if (market == MarketType.Otc)
            {
                throw new Application.Exceptions.RetryableJobException("TPEx 回應缺少 tables 陣列，網站結構可能已變更。");
            }

            return Task.FromResult(Success(date, market));
        });

        var service = new StockHistoryImportService(marketPriceService, _repository, new FakeClock(Today), new FakeLogger());
        var options = new StockHistoryImportOptions { MaxConcurrency = 4, RetryBaseDelaySeconds = 0, MaxRetryCount = 1 };

        var jobId = await service.CreateJobAsync(new StockHistoryImportRequest(MarketScope.All, 3), options, CancellationToken.None);
        await service.RunJobAsync(jobId, new OfficialMarketDataSettings(), options, CancellationToken.None);

        var job = await _repository.GetJobProgressAsync(jobId, CancellationToken.None);
        Assert.NotNull(job);
        Assert.Equal(StockPriceImportJobStatus.CompletedWithErrors, job!.Status);
        Assert.Equal(6, job.TotalTasks);
        Assert.Equal(6, job.CompletedTasks);
        Assert.Equal(3, job.SuccessTasks);
        Assert.Equal(3, job.FailedTasks);
    }

    [Fact]
    public async Task 取消後批次與尚未執行工作狀態皆為已取消()
    {
        using var cts = new CancellationTokenSource();
        var marketPriceService = new FakeMarketPriceService(async (_, date, market, ct) =>
        {
            await cts.CancelAsync();
            ct.ThrowIfCancellationRequested();
            return Success(date, market);
        });

        var service = new StockHistoryImportService(marketPriceService, _repository, new FakeClock(Today), new FakeLogger());
        var options = new StockHistoryImportOptions { MaxConcurrency = 1, RetryBaseDelaySeconds = 0 };

        var jobId = await service.CreateJobAsync(new StockHistoryImportRequest(MarketScope.Listed, 3), options, CancellationToken.None);
        await service.RunJobAsync(jobId, new OfficialMarketDataSettings(), options, cts.Token);

        var job = await _repository.GetJobProgressAsync(jobId, CancellationToken.None);
        Assert.Equal(StockPriceImportJobStatus.Cancelled, job!.Status);

        var tasks = await _repository.GetTaskProgressAsync(jobId, CancellationToken.None);
        Assert.All(tasks, t => Assert.Equal(StockPriceImportTaskStatus.Cancelled, t.Status));
    }

    [Fact]
    public async Task 重新整理後仍可從資料庫讀取最新批次進度()
    {
        var marketPriceService = new FakeMarketPriceService((_, date, market, _) => Task.FromResult(Success(date, market)));
        var service = new StockHistoryImportService(marketPriceService, _repository, new FakeClock(Today), new FakeLogger());
        var options = new StockHistoryImportOptions();

        var jobId = await service.CreateJobAsync(new StockHistoryImportRequest(MarketScope.Listed, 2), options, CancellationToken.None);
        await service.RunJobAsync(jobId, new OfficialMarketDataSettings(), options, CancellationToken.None);

        // 模擬使用者重新開啟畫面：不依賴記憶體狀態，改用新的 Repository 執行個體重新查詢。
        var reopenedRepository = new SqliteStockPriceImportRepository(_databasePath, new FakeClock(Today));
        var latest = await reopenedRepository.GetLatestJobProgressAsync(CancellationToken.None);

        Assert.NotNull(latest);
        Assert.Equal(jobId, latest!.JobId);
        Assert.Equal(StockPriceImportJobStatus.Completed, latest.Status);
        Assert.Equal(100m, latest.ProgressPercent);
    }

    private static OfficialPriceBatchSummary Success(DateOnly date, MarketType market) => new(
        Guid.NewGuid().ToString("N"), OfficialPriceJobType.HistoricalBackfill, date,
        market == MarketType.Listed ? "TWSE" : "TPEx", market, date,
        DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, 1, 1, 0, 0, 0, 0,
        OfficialPriceBatchStatus.Succeeded, null);

    private static OfficialPriceBatchSummary Failed(OfficialPriceJobType jobType, DateOnly date, MarketType market, string errorMessage) => new(
        Guid.NewGuid().ToString("N"), jobType, date,
        market == MarketType.Listed ? "TWSE" : "TPEx", market, null,
        DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, 0, 0, 0, 0, 0, 0,
        OfficialPriceBatchStatus.Failed, errorMessage);

    private static OfficialPriceBatchSummary NotPublished(OfficialPriceJobType jobType, DateOnly date, MarketType market) => new(
        Guid.NewGuid().ToString("N"), jobType, date,
        market == MarketType.Listed ? "TWSE" : "TPEx", market, null,
        DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, 0, 0, 0, 0, 0, 0,
        OfficialPriceBatchStatus.NotPublished, "來源尚未公布指定日期資料。");

    private sealed class FakeMarketPriceService : IMarketPriceService
    {
        private readonly Func<OfficialPriceJobType, DateOnly, MarketType, CancellationToken, Task<OfficialPriceBatchSummary>> _handler;

        public FakeMarketPriceService(Func<OfficialPriceJobType, DateOnly, MarketType, CancellationToken, Task<OfficialPriceBatchSummary>> handler)
            => _handler = handler;

        public Task<IReadOnlyList<OfficialPriceBatchSummary>> FetchAndSaveDailyPricesAsync(DateOnly targetDate, OfficialMarketDataSettings settings, CancellationToken cancellationToken)
            => throw new NotSupportedException();

        public Task<IReadOnlyList<OfficialPriceBatchSummary>> BackfillHistoryAsync(DateOnly targetDate, OfficialMarketDataSettings settings, CancellationToken cancellationToken, Action<string>? reportProgress = null)
            => throw new NotSupportedException();

        public Task<OfficialPriceBatchSummary> FetchAndSaveSingleAsync(OfficialPriceJobType jobType, DateOnly targetDate, MarketType marketType, OfficialMarketDataSettings settings, CancellationToken cancellationToken)
            => _handler(jobType, targetDate, marketType, cancellationToken);
    }

    private sealed class FakeClock : IClock
    {
        private readonly DateOnly _today;
        public FakeClock(DateOnly today) => _today = today;
        public DateTimeOffset GetTaipeiNow() => new(_today.ToDateTime(new TimeOnly(13, 35)), TimeSpan.FromHours(8));
        public DateOnly GetTaipeiToday() => _today;
    }

    private sealed class FakeLogger : IAppLogger
    {
        public void Info(string message) { }
        public void Warning(string message) { }
        public void Error(string message, Exception? exception = null) { }
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try { File.Delete(_databasePath); } catch { /* 測試結束清理，失敗不影響結果 */ }
    }
}
