using YiHeLee.Domain;

namespace YiHeLee.Application.Services;

/// <summary>
/// 盤中／收盤排程的純決策函式（2026-07-13 盤中／收盤流程拆分新增）。
/// 時區固定 Asia/Taipei（由 IClock 保證），時間規則：
/// 09:00:00 ≦ 台北時間 ＜ 13:30:00 為盤中監控時段，依 IntradayCheckIntervalSeconds 週期執行；
/// 13:30 起停止盤中判斷；13:35 執行收盤更新。
/// 全部為無副作用的靜態函式，供 MarketWorkflowScheduleCoordinator 呼叫並可直接單元測試。
/// </summary>
public static class MarketWorkflowPlanner
{
    /// <summary>是否位於盤中監控時段（09:00:00 ≦ time ＜ 13:30:00）。</summary>
    public static bool IsWithinIntradayWindow(TimeOnly time)
        => time >= AppSettings.IntradayMonitoringStartTime && time < AppSettings.IntradayMonitoringEndTime;

    /// <summary>是否已到收盤更新時間（台北時間 ≧ 13:35）。</summary>
    public static bool IsCloseUpdateDue(TimeOnly time) => time >= AppSettings.FixedDailyRunTime;

    /// <summary>是否位於 13:30～13:35 的空窗：不執行盤中判斷，等待 13:35 收盤更新。</summary>
    public static bool IsBetweenIntradayEndAndClose(TimeOnly time)
        => time >= AppSettings.IntradayMonitoringEndTime && time < AppSettings.FixedDailyRunTime;

    public static int ClampIntervalSeconds(int seconds) => Math.Clamp(seconds, 10, 600);

    public static DateTimeOffset GetNextIntradayTickAt(DateTimeOffset lastTickAt, int intervalSeconds)
        => lastTickAt.AddSeconds(ClampIntervalSeconds(intervalSeconds));

    /// <summary>
    /// 是否應在此刻觸發盤中 Tick：
    /// 1. 必須位於盤中監控時段內。
    /// 2. 程式於盤中啟動（尚無任何 Tick）時立即執行一次。
    /// 3. 之後依設定秒數間隔執行，不使用 today-1 或收盤流程資料推估。
    /// 4. 上一次盤中判斷尚未完成時，本分鐘直接略過，不排隊累積、不同時執行兩次。
    /// </summary>
    public static bool ShouldTriggerIntradayTick(
        DateTimeOffset now,
        DateTimeOffset? lastTickAt,
        bool previousTickStillRunning,
        int intervalSeconds)
    {
        if (!IsWithinIntradayWindow(TimeOnly.FromTimeSpan(now.TimeOfDay)))
        {
            return false;
        }

        if (previousTickStillRunning)
        {
            return false;
        }

        if (lastTickAt is null)
        {
            // 啟動後（09:00 整或盤中任意時間）立即執行第一次判斷。
            return true;
        }

        return now >= GetNextIntradayTickAt(lastTickAt.Value, intervalSeconds);
    }

    /// <summary>
    /// 是否應在此刻執行收盤更新（13:35 排程與 13:35 後啟動補跑共用）：
    /// 今日已成功則不重跑；已達每日最大嘗試次數不再重試；
    /// 上一次為不可重試失敗或重試間隔未到時暫不執行。
    /// </summary>
    public static bool ShouldRunCloseUpdate(
        DateTimeOffset now,
        JobRunSummary? latestSummaryForToday,
        int attemptCountToday,
        int maximumDailyAttempts,
        int retryIntervalSeconds)
    {
        if (!IsCloseUpdateDue(TimeOnly.FromTimeSpan(now.TimeOfDay)))
        {
            return false;
        }

        if (attemptCountToday >= maximumDailyAttempts)
        {
            return false;
        }

        if (latestSummaryForToday is null)
        {
            return true;
        }

        if (latestSummaryForToday.Status == JobStatus.Succeeded)
        {
            return false;
        }

        if (latestSummaryForToday.Outcome == RunOutcome.NonRetryableFailure)
        {
            return false;
        }

        var nextRetryAt = latestSummaryForToday.CompletedAt.AddSeconds(ClampIntervalSeconds(retryIntervalSeconds));
        return now >= nextRetryAt;
    }

    /// <summary>
    /// 計算下一次醒來檢查的等待時間：盤中時段以設定秒數為準；
    /// 其他時段最長 30 秒醒來一次（反映設定變更與時段切換），並不早於下一個排程邊界。
    /// </summary>
    public static TimeSpan GetNextWakeDelay(DateTimeOffset now, DateTimeOffset? lastIntradayTickAt, int intradayIntervalSeconds)
    {
        var time = TimeOnly.FromTimeSpan(now.TimeOfDay);
        if (IsWithinIntradayWindow(time))
        {
            if (lastIntradayTickAt is DateTimeOffset last)
            {
                var untilNextTick = GetNextIntradayTickAt(last, intradayIntervalSeconds) - now;
                return untilNextTick <= TimeSpan.Zero ? TimeSpan.FromSeconds(1) : untilNextTick;
            }

            return TimeSpan.FromSeconds(1);
        }

        return TimeSpan.FromSeconds(30);
    }
}
