using YiHeLee.Domain;

namespace YiHeLee.Application.Services;

/// <summary>
/// 盤中／收盤排程的純決策函式（2026-07-13 盤中／收盤流程拆分新增）。
/// 時區固定 Asia/Taipei（由 IClock 保證），時間規則：
/// 09:00:00 ≦ 台北時間 ＜ 13:30:00 為盤中監控時段，每 1 分鐘一次並對齊整分鐘；
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

    /// <summary>把時間截斷到整分鐘（供「同一分鐘只觸發一次」判斷）。</summary>
    public static DateTimeOffset TruncateToMinute(DateTimeOffset time)
        => new(time.Year, time.Month, time.Day, time.Hour, time.Minute, 0, time.Offset);

    /// <summary>取得下一個整分鐘時間點（例如 10:17:30 → 10:18:00），盤中 Tick 一律對齊整分鐘。</summary>
    public static DateTimeOffset GetNextAlignedMinute(DateTimeOffset now)
        => TruncateToMinute(now).AddMinutes(1);

    /// <summary>
    /// 是否應在此刻觸發盤中 Tick：
    /// 1. 必須位於盤中監控時段內。
    /// 2. 程式於盤中啟動（尚無任何 Tick）時立即執行一次，下一次再對齊整分鐘。
    /// 3. 同一個整分鐘只觸發一次。
    /// 4. 上一次盤中判斷尚未完成時，本分鐘直接略過，不排隊累積、不同時執行兩次。
    /// </summary>
    public static bool ShouldTriggerIntradayTick(
        DateTimeOffset now,
        DateTimeOffset? lastTickMinute,
        bool previousTickStillRunning)
    {
        if (!IsWithinIntradayWindow(TimeOnly.FromTimeSpan(now.TimeOfDay)))
        {
            return false;
        }

        if (previousTickStillRunning)
        {
            return false;
        }

        if (lastTickMinute is null)
        {
            // 啟動後（09:00 整或盤中任意時間）立即執行第一次判斷。
            return true;
        }

        return TruncateToMinute(now) > TruncateToMinute(lastTickMinute.Value);
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
        int retryIntervalMinutes)
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

        var nextRetryAt = latestSummaryForToday.CompletedAt.AddMinutes(retryIntervalMinutes);
        return now >= nextRetryAt;
    }

    /// <summary>
    /// 計算下一次醒來檢查的等待時間：盤中時段以「下一個整分鐘」為準；
    /// 其他時段最長 30 秒醒來一次（反映設定變更與時段切換），並不早於下一個排程邊界。
    /// </summary>
    public static TimeSpan GetNextWakeDelay(DateTimeOffset now)
    {
        var time = TimeOnly.FromTimeSpan(now.TimeOfDay);
        if (IsWithinIntradayWindow(time))
        {
            var untilNextMinute = GetNextAlignedMinute(now) - now;
            return untilNextMinute <= TimeSpan.Zero ? TimeSpan.FromSeconds(1) : untilNextMinute;
        }

        return TimeSpan.FromSeconds(30);
    }
}
