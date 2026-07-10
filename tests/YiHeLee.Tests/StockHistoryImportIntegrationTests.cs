using Microsoft.Data.Sqlite;
using Xunit.Abstractions;
using YiHeLee.Application.Abstractions;
using YiHeLee.Application.Services;
using YiHeLee.Domain;
using YiHeLee.Infrastructure.Data;
using YiHeLee.Infrastructure.MarketData;
using YiHeLee.Infrastructure.Time;

namespace YiHeLee.Tests;

/// <summary>
/// 針對「歷史收盤價」手動回補功能的端對端整合測試：實際呼叫 TWSE／TPEx 官方端點、
/// 實際寫入 SQLite，驗證整條路徑（並行排程、Repository 進度、Upsert）可用真實網路運作。
/// 標記 Category=Integration；無網路環境可用 `dotnet test --filter Category!=Integration` 排除。
/// </summary>
[Trait("Category", "Integration")]
public sealed class StockHistoryImportIntegrationTests : IDisposable
{
    private readonly string _databasePath;
    private readonly ITestOutputHelper _output;

    public StockHistoryImportIntegrationTests(ITestOutputHelper output)
    {
        _output = output;
        _databasePath = Path.Combine(Path.GetTempPath(), $"yihelee-import-integration-{Guid.NewGuid():N}.db");
    }

    [Fact]
    public async Task 針對最近1個有效交易日執行歷史回補可從真實官方端點寫入SQLite()
    {
        var clock = new TaipeiClock();
        var logger = new SilentLogger();
        var marketDataRepository = new SqliteMarketDataRepository(_databasePath, clock);
        await marketDataRepository.InitializeAsync();
        var importRepository = new SqliteStockPriceImportRepository(_databasePath, clock);
        await importRepository.InitializeAsync();

        using var httpClient = new HttpClient();
        var twseProvider = new TwseMarketDataProvider(httpClient, logger, clock);
        var tpexProvider = new TpexMarketDataProvider(httpClient, logger, clock);
        var emergingProvider = new EmergingMarketDataProvider(httpClient, logger, clock);
        var marketPriceService = new MarketPriceService(twseProvider, tpexProvider, emergingProvider, marketDataRepository, clock, logger);
        var importService = new StockHistoryImportService(marketPriceService, importRepository, clock, logger);

        var options = new StockHistoryImportOptions { MaxConcurrency = 2 };
        var jobId = await importService.CreateJobAsync(new StockHistoryImportRequest(MarketScope.All, 1), options, CancellationToken.None);
        await importService.RunJobAsync(jobId, new OfficialMarketDataSettings(), options, CancellationToken.None);

        var job = await importRepository.GetJobProgressAsync(jobId, CancellationToken.None);
        Assert.NotNull(job);
        _output.WriteLine($"批次 #{job!.JobId} 狀態={job.Status}，總工作={job.TotalTasks}，成功={job.SuccessTasks}，" +
                           $"略過(休市)={job.SkippedTasks}，失敗={job.FailedTasks}，新增={job.InsertedRows}，更新={job.UpdatedRows}");
        Assert.True(job.Status is StockPriceImportJobStatus.Completed or StockPriceImportJobStatus.CompletedWithErrors,
            $"批次狀態應為完成或部分失敗，實際為 {job.Status}：{job.ErrorMessage}");

        var tasks = await importRepository.GetTaskProgressAsync(jobId, CancellationToken.None);
        Assert.Equal(2, tasks.Count); // 1個有效交易日 × 2個市場（上市、上櫃）
        foreach (var task in tasks)
        {
            _output.WriteLine($"{task.MarketType}／請求日期={task.RequestedDate:yyyy-MM-dd}／實際交易日期={task.ActualTradeDate:yyyy-MM-dd}／" +
                               $"狀態={task.Status}／新增={task.InsertedRows}／更新={task.UpdatedRows}／重試={task.RetryCount}／錯誤={task.ErrorMessage}");
            Assert.True(
                task.Status is StockPriceImportTaskStatus.Succeeded or StockPriceImportTaskStatus.Holiday or StockPriceImportTaskStatus.WaitingForSource,
                $"{task.MarketType} {task.RequestedDate:yyyy-MM-dd} 狀態異常：{task.Status}，{task.ErrorMessage}");
        }

        var latestTradeDate = await marketDataRepository.GetLatestTradeDateAsync(CancellationToken.None);
        _output.WriteLine($"資料庫最新交易日期：{latestTradeDate:yyyy-MM-dd}");
    }

    private sealed class SilentLogger : IAppLogger
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
