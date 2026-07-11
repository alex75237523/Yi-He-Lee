using Microsoft.Data.Sqlite;
using YiHeLee.Application.Abstractions;
using YiHeLee.Domain;
using YiHeLee.Infrastructure.Data;

namespace YiHeLee.Tests;

/// <summary>
/// 2026-07-11 修正回歸測試：使用者手動回溯執行過去日期後，整體最新一筆工作會變成過去日期；
/// 排程器必須改查「指定日期的最新一筆」，否則會誤判「今日尚未執行」而重跑今日，
/// 並把使用者正在查看的回溯結果畫面蓋掉。
/// </summary>
public sealed class SqliteYiHeLeeRepositoryJobSummaryTests : IDisposable
{
    private static readonly DateOnly Today = new(2026, 7, 11);
    private static readonly DateOnly BackdatedDate = new(2026, 7, 9);
    private readonly string _databasePath;
    private readonly SqliteYiHeLeeRepository _repository;

    public SqliteYiHeLeeRepositoryJobSummaryTests()
    {
        _databasePath = Path.Combine(Path.GetTempPath(), $"yihelee-jobsummary-test-{Guid.NewGuid():N}.db");
        _repository = new SqliteYiHeLeeRepository(_databasePath, new FixedClock());
        _repository.InitializeAsync().GetAwaiter().GetResult();
    }

    [Fact]
    public async Task 手動回溯執行後_今日最新一筆仍能正確查得()
    {
        // 今日 13:35 排程執行：休市，不可重試。
        var todayJobId = await _repository.BeginJobAsync(Today, 1, At(13, 35), CancellationToken.None);
        await _repository.CompleteJobAsync(todayJobId, CreateSummary(todayJobId, Today, JobStatus.NoTradingData, RunOutcome.NonRetryableFailure, At(13, 36)), CancellationToken.None);

        // 之後使用者手動回溯執行 7/9 成功；整體最新一筆變成 7/9。
        var backdatedJobId = await _repository.BeginJobAsync(BackdatedDate, 1, At(14, 9), CancellationToken.None);
        await _repository.CompleteJobAsync(backdatedJobId, CreateSummary(backdatedJobId, BackdatedDate, JobStatus.Succeeded, RunOutcome.Success, At(14, 10)), CancellationToken.None);

        var latestOverall = await _repository.GetLatestJobSummaryAsync(CancellationToken.None);
        Assert.NotNull(latestOverall);
        Assert.Equal(BackdatedDate, latestOverall!.TargetDate);

        // 排程器改用本查詢：今日最新一筆必須還是 13:35 的休市紀錄（不可重試），據此不得重跑今日。
        var latestForToday = await _repository.GetLatestJobSummaryForDateAsync(Today, CancellationToken.None);
        Assert.NotNull(latestForToday);
        Assert.Equal(Today, latestForToday!.TargetDate);
        Assert.Equal(JobStatus.NoTradingData, latestForToday.Status);
        Assert.Equal(RunOutcome.NonRetryableFailure, latestForToday.Outcome);
    }

    [Fact]
    public async Task 指定日期沒有任何工作時_回傳null()
    {
        var result = await _repository.GetLatestJobSummaryForDateAsync(Today, CancellationToken.None);
        Assert.Null(result);
    }

    private static JobRunSummary CreateSummary(Guid jobId, DateOnly targetDate, JobStatus status, RunOutcome outcome, DateTimeOffset completedAt) => new(
        jobId, targetDate, status, outcome, "測試", 1, 0, 0, 0, 0, completedAt.AddMinutes(-1), completedAt, []);

    private static DateTimeOffset At(int hour, int minute) => new(Today.ToDateTime(new TimeOnly(hour, minute)), TimeSpan.FromHours(8));

    private sealed class FixedClock : IClock
    {
        public DateTimeOffset GetTaipeiNow() => At(13, 35);
        public DateOnly GetTaipeiToday() => Today;
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try { File.Delete(_databasePath); } catch { /* 測試結束清理，失敗不影響結果 */ }
    }
}
