using Microsoft.Data.Sqlite;
using YiHeLee.Application.Abstractions;
using YiHeLee.Infrastructure.Data;
using YiHeLee.Domain;

namespace YiHeLee.Tests;

/// <summary>
/// 驗證盤中狀態資料表（2026-07-13 盤中／收盤流程拆分）：
/// IntradayAlertState 以唯一鍵 Upsert、跨連線（模擬程式重啟）可恢復；
/// IntradayEvaluationRun 保存每分鐘摘要；Migration 不影響既有 JobRuns 等正式資料表。
/// </summary>
public sealed class SqliteIntradayStateRepositoryTests : IDisposable
{
    private static readonly DateOnly EvaluationDate = new(2026, 7, 14);
    private static readonly DateOnly BaselineDate = new(2026, 7, 13);
    private const string WorkbookPath = @"C:\Data\客戶.xlsx";
    private readonly string _databasePath = Path.Combine(Path.GetTempPath(), $"yihelee-intraday-test-{Guid.NewGuid():N}.db");
    private readonly FakeClock _clock = new();

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        if (File.Exists(_databasePath))
        {
            File.Delete(_databasePath);
        }
    }

    [Fact]
    public async Task 通知狀態以唯一鍵Upsert_同鍵更新不新增重複列()
    {
        var repository = new SqliteIntradayStateRepository(_databasePath, _clock);
        await repository.InitializeAsync();

        var active = CreateState(maWindow: 5, isActive: true);
        await repository.UpsertAlertStatesAsync([active], CancellationToken.None);
        await repository.UpsertAlertStatesAsync(
            [active with { IsActive = false, ClearedAt = _clock.GetTaipeiNow() }], CancellationToken.None);

        var states = await repository.GetAlertStatesAsync(EvaluationDate, WorkbookPath, CancellationToken.None);
        var state = Assert.Single(states);
        Assert.False(state.IsActive);
        Assert.NotNull(state.ClearedAt);
    }

    [Fact]
    public async Task 不同MA條件與不同列的狀態互相獨立保存()
    {
        var repository = new SqliteIntradayStateRepository(_databasePath, _clock);
        await repository.InitializeAsync();

        await repository.UpsertAlertStatesAsync(
        [
            CreateState(maWindow: 5, isActive: true),
            CreateState(maWindow: 20, isActive: true),
            CreateState(maWindow: 120, isActive: true),
            CreateState(maWindow: 5, isActive: true, excelRow: 9)
        ], CancellationToken.None);

        var states = await repository.GetAlertStatesAsync(EvaluationDate, WorkbookPath, CancellationToken.None);
        Assert.Equal(4, states.Count);
        Assert.Equal(3, states.Count(x => x.ExcelRow == 4));
        Assert.Single(states, x => x.ExcelRow == 9 && x.MaWindow == 5);
    }

    [Fact]
    public async Task 程式重啟後可由新連線恢復通知狀態()
    {
        var repository = new SqliteIntradayStateRepository(_databasePath, _clock);
        await repository.InitializeAsync();
        await repository.UpsertAlertStatesAsync([CreateState(maWindow: 20, isActive: true)], CancellationToken.None);

        // 全新的 Repository 實例（模擬程式重啟）必須讀得到先前保存的狀態。
        var restarted = new SqliteIntradayStateRepository(_databasePath, _clock);
        await restarted.InitializeAsync();
        var states = await restarted.GetAlertStatesAsync(EvaluationDate, WorkbookPath, CancellationToken.None);

        var state = Assert.Single(states);
        Assert.True(state.IsActive);
        Assert.Equal(20, state.MaWindow);
        Assert.Equal(BaselineDate, state.BaselineTradeDate);
    }

    [Fact]
    public async Task 盤中執行摘要每分鐘一筆_可依判斷日由新到舊查詢()
    {
        var repository = new SqliteIntradayStateRepository(_databasePath, _clock);
        await repository.InitializeAsync();

        await repository.SaveEvaluationRunAsync(CreateRun(IntradayRunStatus.Succeeded, holdingCount: 12, triggeredCount: 2), CancellationToken.None);
        await repository.SaveEvaluationRunAsync(CreateRun(IntradayRunStatus.Skipped, skippedReason: "收盤更新尚未完成"), CancellationToken.None);

        var runs = await repository.GetEvaluationRunsAsync(EvaluationDate, 10, CancellationToken.None);
        Assert.Equal(2, runs.Count);
        Assert.Equal(IntradayRunStatus.Skipped, runs[0].Status);
        Assert.Equal("收盤更新尚未完成", runs[0].SkippedReason);
        Assert.Equal(IntradayRunStatus.Succeeded, runs[1].Status);
        Assert.Equal(12, runs[1].HoldingCount);
        Assert.Equal(BaselineDate, runs[1].BaselineTradeDate);
    }

    [Fact]
    public async Task Migration只新增盤中資料表_不影響既有JobRuns與StrategyAlerts資料()
    {
        // 先以既有 Repository 建立正式資料表並寫入一筆收盤 Job 紀錄。
        var legacyRepository = new SqliteYiHeLeeRepository(_databasePath, _clock);
        await legacyRepository.InitializeAsync();
        var jobId = await legacyRepository.BeginJobAsync(BaselineDate, 1, _clock.GetTaipeiNow(), CancellationToken.None);

        // 盤中資料表初始化不得刪除或改寫既有資料。
        var repository = new SqliteIntradayStateRepository(_databasePath, _clock);
        await repository.InitializeAsync();

        var attemptCount = await legacyRepository.GetAttemptCountAsync(BaselineDate, CancellationToken.None);
        Assert.Equal(1, attemptCount);
        Assert.NotEqual(Guid.Empty, jobId);

        // 盤中資料表可正常使用。
        await repository.UpsertAlertStatesAsync([CreateState(maWindow: 5, isActive: true)], CancellationToken.None);
        var states = await repository.GetAlertStatesAsync(EvaluationDate, WorkbookPath, CancellationToken.None);
        Assert.Single(states);
    }

    private IntradayAlertStateRecord CreateState(int maWindow, bool isActive, int excelRow = 4) => new(
        EvaluationDate,
        BaselineDate,
        WorkbookPath,
        "客戶頁籤",
        excelRow,
        "2330",
        AlertKind.MovingAverageTriggered,
        maWindow,
        isActive,
        _clock.GetTaipeiNow(),
        _clock.GetTaipeiNow(),
        _clock.GetTaipeiNow(),
        null);

    private IntradayEvaluationRunRecord CreateRun(
        IntradayRunStatus status, int holdingCount = 0, int triggeredCount = 0, string? skippedReason = null) => new(
        0,
        EvaluationDate,
        status == IntradayRunStatus.Skipped ? null : BaselineDate,
        _clock.GetTaipeiNow(),
        _clock.GetTaipeiNow(),
        _clock.GetTaipeiNow(),
        status,
        holdingCount,
        triggeredCount,
        0,
        0,
        0,
        0,
        skippedReason,
        null);

    private sealed class FakeClock : IClock
    {
        public DateTimeOffset GetTaipeiNow() => new(EvaluationDate.ToDateTime(new TimeOnly(10, 31)), TimeSpan.FromHours(8));
        public DateOnly GetTaipeiToday() => EvaluationDate;
    }
}
