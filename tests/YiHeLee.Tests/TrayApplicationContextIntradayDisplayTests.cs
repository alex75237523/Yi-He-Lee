using YiHeLee.App;
using YiHeLee.Domain;

namespace YiHeLee.Tests;

public sealed class TrayApplicationContextIntradayDisplayTests
{
    private static readonly DateOnly EvaluationDate = new(2026, 7, 14);
    private static readonly DateOnly BaselineDate = new(2026, 7, 13);
    private static readonly DateTimeOffset Now = new(2026, 7, 14, 10, 0, 0, TimeSpan.FromHours(8));

    [Fact]
    public void 手動盤中判斷即使沒有新通知_仍要啟動每日結果顯示流程()
    {
        var summary = CreateSummary(isManualRun: true, newNotificationCount: 0, alerts: CreateAlerts(9));

        Assert.True(TrayApplicationContext.ShouldShowIntradayResultWindow(summary, isResultsTabVisible: false));
    }

    [Fact]
    public void 自動盤中判斷沒有新通知時_若每日結果頁已開啟則持續更新內容()
    {
        var summary = CreateSummary(isManualRun: false, newNotificationCount: 0, alerts: CreateAlerts(9));

        Assert.True(TrayApplicationContext.ShouldShowIntradayResultWindow(summary, isResultsTabVisible: true));
        Assert.False(TrayApplicationContext.ShouldShowIntradayResultWindow(summary, isResultsTabVisible: false));
    }

    [Fact]
    public void 盤中結果摘要使用完整Alerts_不是只顯示NewlyTriggeredAlerts()
    {
        var alerts = CreateAlerts(9);
        var summary = CreateSummary(isManualRun: true, newNotificationCount: 0, alerts);

        var jobSummary = TrayApplicationContext.ToJobRunSummary(summary);

        Assert.Equal(9, jobSummary.AlertCount);
        Assert.Equal(9, jobSummary.Alerts.Count);
        Assert.Empty(summary.NewlyTriggeredAlerts);
    }

    private static IntradayRunSummary CreateSummary(bool isManualRun, int newNotificationCount, IReadOnlyList<StrategyAlert> alerts)
        => new(
            EvaluationDate,
            BaselineDate,
            Now,
            Now,
            isManualRun,
            IntradayRunStatus.Succeeded,
            "盤中判斷完成",
            HoldingCount: 351,
            ActiveTriggerCount: alerts.Count,
            NewNotificationCount: newNotificationCount,
            EntryAveragePriceInvalidCount: 0,
            CurrentPriceInvalidCount: 0,
            MissingMovingAverageCount: 0,
            Alerts: alerts,
            NewlyTriggeredAlerts: []);

    private static StrategyAlert[] CreateAlerts(int count)
        => Enumerable.Range(1, count)
            .Select(i => new StrategyAlert(
                EvaluationDate,
                AlertKind.MovingAverageTriggered,
                @"C:\Data\客戶.xlsx",
                "客戶頁籤",
                "測試客戶",
                i,
                $"23{i:00}",
                $"測試股{i}",
                CurrentPrice: 80m,
                Quantity: 1,
                ClosePrice: 100m,
                MovingAverage5: 90m,
                MovingAverage20: 95m,
                MovingAverage60: 98m,
                MovingAverage120: 99m,
                TriggeredMa5: true,
                TriggeredMa20: true,
                TriggeredMa120: true,
                TriggerDescription: "均價已大於或等於進場價/平均價或現價其中一項",
                MarketType: MarketType.Listed,
                IndicatorType: null,
                SourceUrl: null,
                PriceSourceProvider: "TWSE",
                CalculatedAt: Now,
                EntryAveragePrice: 80m,
                EntryAveragePriceIssue: null,
                CurrentPriceIssue: null))
            .ToArray();
}
