using Microsoft.Data.Sqlite;
using YiHeLee.Application.Abstractions;
using YiHeLee.Domain;
using YiHeLee.Infrastructure.Data;

namespace YiHeLee.Tests;

public sealed class SqliteStockPriceImportRepositoryTests : IDisposable
{
    private static readonly DateOnly Today = new(2026, 7, 9);
    private readonly string _databasePath;
    private readonly SqliteStockPriceImportRepository _repository;

    public SqliteStockPriceImportRepositoryTests()
    {
        _databasePath = Path.Combine(Path.GetTempPath(), $"yihelee-importrepo-test-{Guid.NewGuid():N}.db");
        _repository = new SqliteStockPriceImportRepository(_databasePath, new FixedClock());
        _repository.InitializeAsync().GetAwaiter().GetResult();
    }

    [Fact]
    public async Task 建立批次時工作初始狀態皆為排隊中()
    {
        var tasks = BuildDescriptors(3, MarketType.Listed);
        var creation = await _repository.CreateJobAsync(OfficialPriceJobType.HistoricalBackfill, 3, Today, "Asia/Taipei", tasks, Now(), CancellationToken.None);

        var job = await _repository.GetJobProgressAsync(creation.JobId, CancellationToken.None);
        Assert.NotNull(job);
        Assert.Equal(StockPriceImportJobStatus.Queued, job!.Status);
        Assert.Equal(3, job.TotalTasks);
        Assert.Equal(0, job.CompletedTasks);

        var taskProgress = await _repository.GetTaskProgressAsync(creation.JobId, CancellationToken.None);
        Assert.All(taskProgress, t => Assert.Equal(StockPriceImportTaskStatus.Queued, t.Status));
    }

    [Fact]
    public async Task 同一批次相同市場與日期重複時違反唯一鍵()
    {
        var duplicated = new[]
        {
            new StockPriceImportTaskDescriptor(MarketType.Listed, Today.AddDays(-1)),
            new StockPriceImportTaskDescriptor(MarketType.Listed, Today.AddDays(-1))
        };

        await Assert.ThrowsAsync<SqliteException>(() =>
            _repository.CreateJobAsync(OfficialPriceJobType.HistoricalBackfill, 1, Today, "Asia/Taipei", duplicated, Now(), CancellationToken.None));
    }

    [Fact]
    public async Task 工作完成後批次進度依明細彙總且百分比正確()
    {
        var tasks = BuildDescriptors(2, MarketType.Listed);
        var creation = await _repository.CreateJobAsync(OfficialPriceJobType.HistoricalBackfill, 2, Today, "Asia/Taipei", tasks, Now(), CancellationToken.None);
        var taskId = creation.TaskIds.Values.First();

        await _repository.StartTaskAsync(taskId, Now(), CancellationToken.None);
        await _repository.CompleteTaskAsync(
            taskId, StockPriceImportTaskStatus.Succeeded, Today.AddDays(-1), "https://example.invalid",
            retryCount: 0, totalRows: 500, insertedRows: 500, updatedRows: 0, skippedRows: 0, failedRows: 0,
            completedAt: Now(), errorMessage: null, CancellationToken.None);

        var job = await _repository.GetJobProgressAsync(creation.JobId, CancellationToken.None);
        Assert.Equal(1, job!.CompletedTasks);
        Assert.Equal(1, job.SuccessTasks);
        Assert.Equal(500, job.InsertedRows);
        Assert.Equal(50m, job.ProgressPercent); // 2工作中完成1個
    }

    [Fact]
    public async Task 全部成功時最終狀態為完成()
    {
        var creation = await _repository.CreateJobAsync(OfficialPriceJobType.HistoricalBackfill, 1, Today, "Asia/Taipei",
            BuildDescriptors(1, MarketType.Listed), Now(), CancellationToken.None);
        var taskId = creation.TaskIds.Values.First();

        await CompleteAsync(taskId, StockPriceImportTaskStatus.Succeeded);
        await _repository.FinalizeJobAsync(creation.JobId, Now(), CancellationToken.None);

        var job = await _repository.GetJobProgressAsync(creation.JobId, CancellationToken.None);
        Assert.Equal(StockPriceImportJobStatus.Completed, job!.Status);
    }

    [Fact]
    public async Task 全部失敗時最終狀態為失敗()
    {
        var creation = await _repository.CreateJobAsync(OfficialPriceJobType.HistoricalBackfill, 1, Today, "Asia/Taipei",
            BuildDescriptors(1, MarketType.Listed), Now(), CancellationToken.None);
        var taskId = creation.TaskIds.Values.First();

        await CompleteAsync(taskId, StockPriceImportTaskStatus.Failed);
        await _repository.FinalizeJobAsync(creation.JobId, Now(), CancellationToken.None);

        var job = await _repository.GetJobProgressAsync(creation.JobId, CancellationToken.None);
        Assert.Equal(StockPriceImportJobStatus.Failed, job!.Status);
    }

    [Fact]
    public async Task 部分成功部分失敗時最終狀態為部分失敗()
    {
        var tasks = BuildDescriptors(2, MarketType.Listed);
        var creation = await _repository.CreateJobAsync(OfficialPriceJobType.HistoricalBackfill, 2, Today, "Asia/Taipei", tasks, Now(), CancellationToken.None);
        var taskIds = creation.TaskIds.Values.ToArray();

        await CompleteAsync(taskIds[0], StockPriceImportTaskStatus.Succeeded);
        await CompleteAsync(taskIds[1], StockPriceImportTaskStatus.Failed);
        await _repository.FinalizeJobAsync(creation.JobId, Now(), CancellationToken.None);

        var job = await _repository.GetJobProgressAsync(creation.JobId, CancellationToken.None);
        Assert.Equal(StockPriceImportJobStatus.CompletedWithErrors, job!.Status);
    }

    [Fact]
    public async Task 取消批次後仍在排隊或執行中的工作皆標記為已取消()
    {
        var tasks = BuildDescriptors(3, MarketType.Listed);
        var creation = await _repository.CreateJobAsync(OfficialPriceJobType.HistoricalBackfill, 3, Today, "Asia/Taipei", tasks, Now(), CancellationToken.None);
        var taskIds = creation.TaskIds.Values.ToArray();

        await CompleteAsync(taskIds[0], StockPriceImportTaskStatus.Succeeded);
        await _repository.StartTaskAsync(taskIds[1], Now(), CancellationToken.None); // 尚在執行中

        await _repository.CancelJobAsync(creation.JobId, Now(), CancellationToken.None);

        var job = await _repository.GetJobProgressAsync(creation.JobId, CancellationToken.None);
        Assert.Equal(StockPriceImportJobStatus.Cancelled, job!.Status);

        var taskProgress = await _repository.GetTaskProgressAsync(creation.JobId, CancellationToken.None);
        Assert.Equal(StockPriceImportTaskStatus.Succeeded, taskProgress.Single(t => t.TaskId == taskIds[0]).Status);
        Assert.Equal(StockPriceImportTaskStatus.Cancelled, taskProgress.Single(t => t.TaskId == taskIds[1]).Status);
        Assert.Equal(StockPriceImportTaskStatus.Cancelled, taskProgress.Single(t => t.TaskId == taskIds[2]).Status);
    }

    [Fact]
    public async Task 取得最新批次回傳最近建立的一筆()
    {
        var first = await _repository.CreateJobAsync(OfficialPriceJobType.HistoricalBackfill, 1, Today, "Asia/Taipei",
            BuildDescriptors(1, MarketType.Listed), Now(), CancellationToken.None);
        var second = await _repository.CreateJobAsync(OfficialPriceJobType.HistoricalBackfill, 1, Today, "Asia/Taipei",
            BuildDescriptors(1, MarketType.Otc), Now(), CancellationToken.None);

        var latest = await _repository.GetLatestJobProgressAsync(CancellationToken.None);

        Assert.NotNull(latest);
        Assert.Equal(second.JobId, latest!.JobId);
        Assert.NotEqual(first.JobId, latest.JobId);
    }

    private async Task CompleteAsync(long taskId, StockPriceImportTaskStatus status)
    {
        await _repository.StartTaskAsync(taskId, Now(), CancellationToken.None);
        await _repository.CompleteTaskAsync(
            taskId, status, Today.AddDays(-1), "https://example.invalid",
            retryCount: 0, totalRows: 1, insertedRows: status == StockPriceImportTaskStatus.Succeeded ? 1 : 0,
            updatedRows: 0, skippedRows: 0, failedRows: status == StockPriceImportTaskStatus.Failed ? 1 : 0,
            completedAt: Now(), errorMessage: status == StockPriceImportTaskStatus.Failed ? "測試失敗" : null,
            CancellationToken.None);
    }

    private static IReadOnlyList<StockPriceImportTaskDescriptor> BuildDescriptors(int count, MarketType marketType)
        => Enumerable.Range(1, count).Select(i => new StockPriceImportTaskDescriptor(marketType, Today.AddDays(-i))).ToArray();

    private static DateTimeOffset Now() => new(Today.ToDateTime(new TimeOnly(13, 35)), TimeSpan.FromHours(8));

    private sealed class FixedClock : IClock
    {
        public DateTimeOffset GetTaipeiNow() => Now();
        public DateOnly GetTaipeiToday() => Today;
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try { File.Delete(_databasePath); } catch { /* 測試結束清理，失敗不影響結果 */ }
    }
}
