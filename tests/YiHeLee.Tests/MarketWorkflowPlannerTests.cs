using YiHeLee.Application.Services;
using YiHeLee.Domain;

namespace YiHeLee.Tests;

/// <summary>
/// 驗證盤中／收盤排程決策（2026-07-13 盤中／收盤流程拆分）：
/// 09:00 第一次判斷、盤中啟動立即判斷一次、之後對齊整分鐘、13:30 停止盤中、13:35 收盤更新、
/// 上一 Tick 未完成時直接略過、13:35 後啟動依今日是否已成功決定補跑。
/// </summary>
public sealed class MarketWorkflowPlannerTests
{
    private static readonly DateOnly Day = new(2026, 7, 14);
    private static readonly TimeSpan Tz = TimeSpan.FromHours(8);

    private static DateTimeOffset At(int hour, int minute, int second = 0)
        => new(Day.ToDateTime(new TimeOnly(hour, minute, second)), Tz);

    [Theory]
    [InlineData(9, 0, 0, true)]    // 09:00 開始
    [InlineData(8, 59, 59, false)] // 開盤前
    [InlineData(13, 29, 59, true)] // 13:30 前最後一秒仍在盤中
    [InlineData(13, 30, 0, false)] // 13:30 起停止盤中判斷
    [InlineData(13, 35, 0, false)]
    public void 盤中監控時段為0900含至1330不含(int hour, int minute, int second, bool expected)
        => Assert.Equal(expected, MarketWorkflowPlanner.IsWithinIntradayWindow(new TimeOnly(hour, minute, second)));

    [Theory]
    [InlineData(13, 35, 0, true)]
    [InlineData(13, 34, 59, false)]
    [InlineData(15, 0, 0, true)]
    public void 收盤更新於1335起執行(int hour, int minute, int second, bool expected)
        => Assert.Equal(expected, MarketWorkflowPlanner.IsCloseUpdateDue(new TimeOnly(hour, minute, second)));

    [Theory]
    [InlineData(13, 30, 0, true)]
    [InlineData(13, 32, 0, true)]
    [InlineData(13, 34, 59, true)]
    [InlineData(13, 35, 0, false)]
    [InlineData(13, 29, 59, false)]
    public void 於1330至1335之間不執行盤中判斷等待收盤更新(int hour, int minute, int second, bool expected)
        => Assert.Equal(expected, MarketWorkflowPlanner.IsBetweenIntradayEndAndClose(new TimeOnly(hour, minute, second)));

    [Fact]
    public void 於0900整啟動第一次盤中判斷()
        => Assert.True(MarketWorkflowPlanner.ShouldTriggerIntradayTick(At(9, 0), lastTickMinute: null, previousTickStillRunning: false));

    [Fact]
    public void 程式於盤中啟動時立即執行一次盤中判斷()
        => Assert.True(MarketWorkflowPlanner.ShouldTriggerIntradayTick(At(10, 17, 30), lastTickMinute: null, previousTickStillRunning: false));

    [Fact]
    public void 下一次判斷對齊整分鐘()
    {
        Assert.Equal(At(10, 18), MarketWorkflowPlanner.GetNextAlignedMinute(At(10, 17, 30)));

        // 10:17:30 已執行過（記為 10:17 這一分鐘）：10:17:45 不再觸發，10:18:00 才觸發。
        var lastTick = At(10, 17, 30);
        Assert.False(MarketWorkflowPlanner.ShouldTriggerIntradayTick(At(10, 17, 45), lastTick, previousTickStillRunning: false));
        Assert.True(MarketWorkflowPlanner.ShouldTriggerIntradayTick(At(10, 18, 0), lastTick, previousTickStillRunning: false));
    }

    [Fact]
    public void 於1330起不再觸發盤中判斷()
        => Assert.False(MarketWorkflowPlanner.ShouldTriggerIntradayTick(At(13, 30), At(13, 29), previousTickStillRunning: false));

    [Fact]
    public void 上一次盤中判斷尚未完成時_下一Tick直接略過不排隊()
        => Assert.False(MarketWorkflowPlanner.ShouldTriggerIntradayTick(At(10, 18), At(10, 17), previousTickStillRunning: true));

    [Fact]
    public void 非交易日不啟動盤中判斷由排程器以已知非交易日判定()
    {
        // 排程器在觸發前先查 ITradingDateResolver.IsKnownNonTradingDayAsync（見 TradingDateResolverTests：
        // 週六／週日與官方批次記錄休市的日期回傳 true）；本測試確認決策函式本身與日期無關、只看時間，
        // 因此非交易日的防線在排程器層，測試於 MarketWorkflowScheduleCoordinator 的呼叫流程中生效。
        Assert.True(MarketWorkflowPlanner.ShouldTriggerIntradayTick(At(10, 0), null, false));
    }

    [Fact]
    public void 於1335後啟動且今日收盤尚未成功時補跑()
        => Assert.True(MarketWorkflowPlanner.ShouldRunCloseUpdate(
            At(14, 10), latestSummaryForToday: null, attemptCountToday: 0,
            maximumDailyAttempts: 12, retryIntervalMinutes: 10));

    [Fact]
    public void 於1335後啟動且今日已成功時不重跑()
        => Assert.False(MarketWorkflowPlanner.ShouldRunCloseUpdate(
            At(14, 10), CreateSummary(JobStatus.Succeeded, RunOutcome.Success, At(13, 40)), attemptCountToday: 1,
            maximumDailyAttempts: 12, retryIntervalMinutes: 10));

    [Fact]
    public void 未到1335不執行收盤更新()
        => Assert.False(MarketWorkflowPlanner.ShouldRunCloseUpdate(
            At(13, 34, 59), latestSummaryForToday: null, attemptCountToday: 0,
            maximumDailyAttempts: 12, retryIntervalMinutes: 10));

    [Fact]
    public void 可重試失敗需等重試間隔到才重跑()
    {
        var failedAt = At(13, 40);
        var failed = CreateSummary(JobStatus.WebsiteNotUpdated, RunOutcome.RetryableFailure, failedAt);

        Assert.False(MarketWorkflowPlanner.ShouldRunCloseUpdate(At(13, 45), failed, 1, 12, 10));
        Assert.True(MarketWorkflowPlanner.ShouldRunCloseUpdate(At(13, 50), failed, 1, 12, 10));
    }

    [Fact]
    public void 不可重試失敗與已達當日上限時不再重跑()
    {
        var nonRetryable = CreateSummary(JobStatus.ValidationFailed, RunOutcome.NonRetryableFailure, At(13, 40));
        Assert.False(MarketWorkflowPlanner.ShouldRunCloseUpdate(At(15, 0), nonRetryable, 1, 12, 10));

        var retryable = CreateSummary(JobStatus.WebsiteNotUpdated, RunOutcome.RetryableFailure, At(13, 40));
        Assert.False(MarketWorkflowPlanner.ShouldRunCloseUpdate(At(15, 0), retryable, attemptCountToday: 12, maximumDailyAttempts: 12, retryIntervalMinutes: 10));
    }

    [Fact]
    public void 盤中時段的下一次醒來時間對齊整分鐘()
    {
        var delay = MarketWorkflowPlanner.GetNextWakeDelay(At(10, 17, 30));
        Assert.Equal(TimeSpan.FromSeconds(30), delay);

        var delayFromWholeMinute = MarketWorkflowPlanner.GetNextWakeDelay(At(10, 17, 0));
        Assert.Equal(TimeSpan.FromMinutes(1), delayFromWholeMinute);
    }

    private static JobRunSummary CreateSummary(JobStatus status, RunOutcome outcome, DateTimeOffset completedAt)
        => new(Guid.NewGuid(), Day, status, outcome, "測試", 1, 0, 0, 0, 0, completedAt, completedAt, [], []);
}
