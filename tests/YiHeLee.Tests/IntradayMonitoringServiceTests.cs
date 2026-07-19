using YiHeLee.Application.Abstractions;
using YiHeLee.Application.Services;
using YiHeLee.Domain;

namespace YiHeLee.Tests;

/// <summary>
/// 驗證盤中自動判斷（2026-07-13 盤中／收盤流程拆分）的流程隔離與通知去重：
/// 盤中 Tick 只讀取已保存的上一交易日 StockMovingAverage 與 Excel 當下進場價／DDE 現價，
/// 不呼叫官方 Provider、不計算均線、不寫入 StockMovingAverage、不寫入 Excel 頁籤
/// （本測試以 Spy 驗證，且 IntradayMonitoringService 建構子根本不依賴這些元件）；
/// 同一條件持續成立只通知一次，成立→不成立記錄清除，重啟後由 SQLite 狀態恢復不重複通知。
/// </summary>
public sealed class IntradayMonitoringServiceTests
{
    private static readonly DateOnly BaselineDate = new(2026, 7, 13);   // 星期一（上一交易日）
    private static readonly DateOnly EvaluationDate = new(2026, 7, 14); // 星期二（盤中判斷日）
    private const string WorkbookPath = @"C:\Data\客戶.xlsx";

    [Fact]
    public async Task 盤中判斷只用上一交易日均價_不抓官方資料_不算均線_不寫Excel()
    {
        var fixture = CreateFixture();
        fixture.Excel.Holdings = [CreateHolding("2330", "台積電", entryAveragePrice: 880m, currentPrice: 840m, excelRow: 4)];

        var summary = await fixture.Service.RunOnceAsync(isManualRun: false, fixture.Clock.GetTaipeiNow(), CancellationToken.None);

        Assert.Equal(IntradayRunStatus.Succeeded, summary.Status);
        Assert.Equal(EvaluationDate, summary.EvaluationDate);
        Assert.Equal(BaselineDate, summary.BaselineTradeDate);

        // 只讀上一交易日的均價快照，禁止讀今天（今日新均價下一交易日才可用）。
        var requestedDate = Assert.Single(fixture.MarketData.RequestedMovingAverageDates);
        Assert.Equal(BaselineDate, requestedDate);

        // 盤中禁止：計算或覆蓋均價快照、寫入 Excel 頁籤。
        Assert.Equal(0, fixture.MarketData.SaveMovingAverageCallCount);
        Assert.Equal(0, fixture.Excel.WriteCallCount);
        Assert.Equal(1, fixture.Excel.ReadCallCount);

        // 通知（StrategyAlert.TradeDate）保存 EvaluationDate；訊息同時包含兩個日期。
        var alert = Assert.Single(summary.Alerts, x => x.AlertKind == AlertKind.MovingAverageTriggered);
        Assert.Equal(EvaluationDate, alert.TradeDate);
        Assert.Contains("EvaluationDate=2026-07-14", summary.Message, StringComparison.Ordinal);
        Assert.Contains("BaselineTradeDate=2026-07-13", summary.Message, StringComparison.Ordinal);

        // 執行摘要保存兩個日期，供追查「判斷的是哪一天、用的是哪一天的均價」。
        var run = Assert.Single(fixture.StateRepository.SavedRuns);
        Assert.Equal(EvaluationDate, run.EvaluationDate);
        Assert.Equal(BaselineDate, run.BaselineTradeDate);
        Assert.Equal(1, run.HoldingCount);
        Assert.Equal(1, run.TriggeredCount);
    }

    [Fact]
    public async Task 盤中判斷使用Excel當下的進場價與DDE現價()
    {
        var fixture = CreateFixture();
        fixture.Excel.Holdings = [CreateHolding("2330", "台積電", entryAveragePrice: 880.5m, currentPrice: 842.25m, excelRow: 4)];

        var summary = await fixture.Service.RunOnceAsync(isManualRun: false, fixture.Clock.GetTaipeiNow(), CancellationToken.None);

        var alert = Assert.Single(summary.Alerts, x => x.AlertKind == AlertKind.MovingAverageTriggered);
        Assert.Equal(880.5m, alert.EntryAveragePrice);
        Assert.Equal(842.25m, alert.CurrentPrice);
    }

    [Fact]
    public async Task 盤中判斷執行時_會回報目前步驟與進度條百分比()
    {
        var fixture = CreateFixture();
        fixture.Excel.Holdings = [CreateHolding("2330", "台積電", entryAveragePrice: 880m, currentPrice: 840m, excelRow: 4)];

        var summary = await fixture.Service.RunOnceAsync(isManualRun: true, fixture.Clock.GetTaipeiNow(), CancellationToken.None);

        Assert.True(summary.IsManualRun);
        Assert.Contains(fixture.UserInteraction.Statuses, x => x.Message.Contains("開始執行", StringComparison.Ordinal) && x.Percent == 5);
        Assert.Contains(fixture.UserInteraction.Statuses, x => x.Message.Contains("讀取設定", StringComparison.Ordinal) && x.Percent == 8);
        Assert.Contains(fixture.UserInteraction.Statuses, x => x.Message.Contains("讀取 Excel 客戶持股", StringComparison.Ordinal) && x.Percent == 60);
        Assert.Contains(fixture.UserInteraction.Statuses, x => x.Message.Contains("比對均價條件", StringComparison.Ordinal) && x.Percent == 82);
        Assert.Contains(fixture.UserInteraction.Statuses, x => x.Message.Contains("更新通知去重狀態", StringComparison.Ordinal) && x.Percent == 92);
        Assert.Contains(fixture.UserInteraction.Statuses, x => x.Message.Contains("盤中判斷完成", StringComparison.Ordinal) && x.Percent == 100);
        Assert.Contains(fixture.UserInteraction.ProgressDetails, detail => detail == string.Empty);
    }

    [Fact]
    public async Task 一筆DDE異常只影響該持股_其他持股照常判斷()
    {
        var fixture = CreateFixture();
        fixture.Excel.Holdings =
        [
            CreateHolding("2330", "台積電", entryAveragePrice: 880m, currentPrice: 840m, excelRow: 4),
            CreateHolding("5351", "鈺創", entryAveragePrice: 100m, currentPrice: null, currentPriceIssue: "#N/A（DDE 尚未取得資料）", excelRow: 5)
        ];

        var summary = await fixture.Service.RunOnceAsync(isManualRun: false, fixture.Clock.GetTaipeiNow(), CancellationToken.None);

        Assert.Equal(IntradayRunStatus.PartialSuccess, summary.Status);
        Assert.Contains(summary.Alerts, x => x.StockCode == "2330" && x.AlertKind == AlertKind.MovingAverageTriggered);
        Assert.Contains(summary.Alerts, x => x.StockCode == "5351" && x.AlertKind == AlertKind.CurrentPriceInvalid);
        Assert.Equal(1, summary.CurrentPriceInvalidCount);
        Assert.Equal(1, summary.ActiveTriggerCount);
    }

    [Fact]
    public async Task 條件由不成立變成立時通知_持續成立不重複通知_變不成立記錄清除_再次成立可再通知()
    {
        var fixture = CreateFixture();

        // 第一分鐘：成立 → 新通知。
        fixture.Excel.Holdings = [CreateHolding("2330", "台積電", entryAveragePrice: 880m, currentPrice: 840m, excelRow: 4)];
        var tick1 = await fixture.Service.RunOnceAsync(false, fixture.Clock.GetTaipeiNow(), CancellationToken.None);
        Assert.Equal(1, tick1.NewNotificationCount);

        // 第二分鐘：持續成立 → 不重複通知，只更新狀態。
        fixture.Clock.Advance(TimeSpan.FromMinutes(1));
        var tick2 = await fixture.Service.RunOnceAsync(false, fixture.Clock.GetTaipeiNow(), CancellationToken.None);
        Assert.Equal(0, tick2.NewNotificationCount);
        Assert.Equal(1, tick2.ActiveTriggerCount);
        Assert.All(fixture.StateRepository.GetStatesSnapshot().Where(x => x.AlertKind == AlertKind.MovingAverageTriggered),
            state => Assert.True(state.IsActive));

        // 第三分鐘：現價 940 不低於 MA5 860（第二條不成立）→ 複合條件不成立，記錄清除。
        fixture.Clock.Advance(TimeSpan.FromMinutes(1));
        fixture.Excel.Holdings = [CreateHolding("2330", "台積電", entryAveragePrice: 950m, currentPrice: 940m, excelRow: 4)];
        var tick3 = await fixture.Service.RunOnceAsync(false, fixture.Clock.GetTaipeiNow(), CancellationToken.None);
        Assert.Equal(0, tick3.NewNotificationCount);
        Assert.Equal(0, tick3.ActiveTriggerCount);
        Assert.All(fixture.StateRepository.GetStatesSnapshot().Where(x => x.AlertKind == AlertKind.MovingAverageTriggered),
            state =>
            {
                Assert.False(state.IsActive);
                Assert.NotNull(state.ClearedAt);
            });

        // 第四分鐘：再次成立 → 可以再次通知。
        fixture.Clock.Advance(TimeSpan.FromMinutes(1));
        fixture.Excel.Holdings = [CreateHolding("2330", "台積電", entryAveragePrice: 880m, currentPrice: 840m, excelRow: 4)];
        var tick4 = await fixture.Service.RunOnceAsync(false, fixture.Clock.GetTaipeiNow(), CancellationToken.None);
        Assert.Equal(1, tick4.NewNotificationCount);
    }

    [Fact]
    public async Task 程式重啟後從SQLite恢復狀態_持續成立的條件不重複通知()
    {
        var fixture = CreateFixture();
        fixture.Excel.Holdings = [CreateHolding("2330", "台積電", entryAveragePrice: 880m, currentPrice: 840m, excelRow: 4)];
        var tick1 = await fixture.Service.RunOnceAsync(false, fixture.Clock.GetTaipeiNow(), CancellationToken.None);
        Assert.Equal(1, tick1.NewNotificationCount);

        // 以同一個狀態儲存庫建立全新的 Service 實例，模擬程式重啟。
        var restarted = CreateFixture(fixture.StateRepository, fixture.Clock);
        restarted.Excel.Holdings = [CreateHolding("2330", "台積電", entryAveragePrice: 880m, currentPrice: 840m, excelRow: 4)];
        restarted.Clock.Advance(TimeSpan.FromMinutes(1));
        var tick2 = await restarted.Service.RunOnceAsync(false, restarted.Clock.GetTaipeiNow(), CancellationToken.None);

        Assert.Equal(0, tick2.NewNotificationCount);
        Assert.Equal(1, tick2.ActiveTriggerCount);
    }

    [Fact]
    public async Task 不同持股列的複合通知狀態互相獨立_每列一筆MaWindow0狀態_不互相覆蓋()
    {
        var fixture = CreateFixture();
        // 基準均價 MA5=860、MA20=870。複合成立需要 進場價 > 870 且 現價 < 860。
        // A（列 4）：進場價 880 > 870、現價 840 < 860 → 複合成立（單一 MaWindow=0 狀態）。
        // B（列 5）：進場價 880 > 870、現價 855 < 860 → 複合成立（獨立的單一狀態）。
        fixture.Excel.Holdings =
        [
            CreateHolding("2330", "台積電", entryAveragePrice: 880m, currentPrice: 840m, excelRow: 4),
            CreateHolding("2330", "台積電", entryAveragePrice: 880m, currentPrice: 855m, excelRow: 5)
        ];

        var tick1 = await fixture.Service.RunOnceAsync(false, fixture.Clock.GetTaipeiNow(), CancellationToken.None);
        Assert.Equal(2, tick1.NewNotificationCount);

        var states = fixture.StateRepository.GetStatesSnapshot()
            .Where(x => x.AlertKind == AlertKind.MovingAverageTriggered)
            .ToArray();
        // 2026-07-19 起每列複合成立只會有一筆 MaWindow=0 狀態，不再依 MA 天數分成多筆。
        Assert.All(states, s => Assert.Equal(0, s.MaWindow));
        Assert.Single(states, x => x.ExcelRow == 4 && x.MaWindow == 0 && x.IsActive);
        Assert.Single(states, x => x.ExcelRow == 5 && x.MaWindow == 0 && x.IsActive);

        // B 的現價 940 不低於 MA5 860（第二條不成立）→ 複合不成立，只清除 B 的狀態，A 不受影響、也不重複通知。
        fixture.Clock.Advance(TimeSpan.FromMinutes(1));
        fixture.Excel.Holdings =
        [
            CreateHolding("2330", "台積電", entryAveragePrice: 880m, currentPrice: 840m, excelRow: 4),
            CreateHolding("2330", "台積電", entryAveragePrice: 950m, currentPrice: 940m, excelRow: 5)
        ];
        var tick2 = await fixture.Service.RunOnceAsync(false, fixture.Clock.GetTaipeiNow(), CancellationToken.None);

        Assert.Equal(0, tick2.NewNotificationCount);
        states = fixture.StateRepository.GetStatesSnapshot()
            .Where(x => x.AlertKind == AlertKind.MovingAverageTriggered)
            .ToArray();
        Assert.Single(states, x => x.ExcelRow == 4 && x.MaWindow == 0 && x.IsActive);
        Assert.Single(states, x => x.ExcelRow == 5 && x.MaWindow == 0 && !x.IsActive && x.ClearedAt is not null);
    }

    [Fact]
    public async Task 舊版當日MaWindow5_20_120殘留狀態_首次執行新版時被清除_不阻擋新的複合通知()
    {
        var fixture = CreateFixture();
        fixture.Excel.Holdings = [CreateHolding("2330", "台積電", entryAveragePrice: 880m, currentPrice: 840m, excelRow: 4)];

        // 模擬舊版本當日殘留的 MaWindow=5／20／120 Active 狀態（同一持股列）。
        var now = fixture.Clock.GetTaipeiNow();
        await fixture.StateRepository.UpsertAlertStatesAsync(
        [
            LegacyMovingAverageState(maWindow: 5, now),
            LegacyMovingAverageState(maWindow: 20, now),
            LegacyMovingAverageState(maWindow: 120, now)
        ], CancellationToken.None);

        var tick = await fixture.Service.RunOnceAsync(false, fixture.Clock.GetTaipeiNow(), CancellationToken.None);

        // 新的 MaWindow=0 複合條件視為全新通知，不被舊 MaWindow=5／20／120 狀態阻擋。
        Assert.Equal(1, tick.NewNotificationCount);

        var states = fixture.StateRepository.GetStatesSnapshot()
            .Where(x => x.AlertKind == AlertKind.MovingAverageTriggered)
            .ToArray();
        Assert.Single(states, x => x.MaWindow == 0 && x.IsActive);
        // 舊 5／20／120 狀態第一次執行新版時被清除，不再繼續影響。
        Assert.All(states.Where(x => x.MaWindow != 0), s =>
        {
            Assert.False(s.IsActive);
            Assert.NotNull(s.ClearedAt);
        });
    }

    [Fact]
    public async Task 手動立即盤中判斷與排程盤中判斷使用完全相同的複合公式()
    {
        var manual = CreateFixture();
        manual.Excel.Holdings = [CreateHolding("2330", "台積電", entryAveragePrice: 880m, currentPrice: 840m, excelRow: 4)];
        var manualSummary = await manual.Service.RunOnceAsync(isManualRun: true, manual.Clock.GetTaipeiNow(), CancellationToken.None);

        var scheduled = CreateFixture();
        scheduled.Excel.Holdings = [CreateHolding("2330", "台積電", entryAveragePrice: 880m, currentPrice: 840m, excelRow: 4)];
        var scheduledSummary = await scheduled.Service.RunOnceAsync(isManualRun: false, scheduled.Clock.GetTaipeiNow(), CancellationToken.None);

        Assert.True(manualSummary.IsManualRun);
        Assert.False(scheduledSummary.IsManualRun);
        Assert.Equal(scheduledSummary.ActiveTriggerCount, manualSummary.ActiveTriggerCount);

        var manualAlert = Assert.Single(manualSummary.Alerts, x => x.AlertKind == AlertKind.MovingAverageTriggered);
        var scheduledAlert = Assert.Single(scheduledSummary.Alerts, x => x.AlertKind == AlertKind.MovingAverageTriggered);
        Assert.Equal(scheduledAlert.TriggeredMa5, manualAlert.TriggeredMa5);
        Assert.Equal(scheduledAlert.TriggeredMa20, manualAlert.TriggeredMa20);
        Assert.Equal(scheduledAlert.TriggerDescription, manualAlert.TriggerDescription);
    }

    [Fact]
    public async Task 基準均價未就緒時_不讀Excel_不產生通知_明確記錄原因()
    {
        var fixture = CreateFixture();
        fixture.Resolver.Resolution = new IntradayBaselineResolution(
            EvaluationDate, null, false,
            "上一交易日 2026-07-13 的均價快照不存在或不完整，禁止退回更舊的均價快照。請先執行收盤更新或歷史回補。",
            BaselineDate, null);

        var summary = await fixture.Service.RunOnceAsync(false, fixture.Clock.GetTaipeiNow(), CancellationToken.None);

        Assert.Equal(IntradayRunStatus.BaselineNotReady, summary.Status);
        Assert.Empty(summary.Alerts);
        Assert.Equal(0, fixture.Excel.ReadCallCount);
        Assert.Contains("基準均價資料尚未就緒", summary.Message, StringComparison.Ordinal);
        Assert.Contains("請先執行收盤更新或歷史回補", summary.Message, StringComparison.Ordinal);

        var run = Assert.Single(fixture.StateRepository.SavedRuns);
        Assert.Equal(IntradayRunStatus.BaselineNotReady, run.Status);
        Assert.Null(run.BaselineTradeDate);
    }

    [Fact]
    public async Task 基準缺漏時_第一次執行完成準備後_同一次呼叫繼續讀Excel並判斷()
    {
        var preparation = new FakeBaselinePreparationService();
        var fixture = CreateFixture(baselinePreparationService: preparation);
        fixture.Resolver.Resolutions.Enqueue(new IntradayBaselineResolution(
            EvaluationDate, null, false,
            "上一交易日均價快照不存在。",
            BaselineDate, null, BaselineDate));
        fixture.Resolver.Resolutions.Enqueue(new IntradayBaselineResolution(
            EvaluationDate, BaselineDate, true, null,
            BaselineDate, BaselineDate, BaselineDate));
        fixture.Excel.Holdings = [CreateHolding("2330", "台積電", entryAveragePrice: 880m, currentPrice: 840m, excelRow: 4)];

        var summary = await fixture.Service.RunOnceAsync(false, fixture.Clock.GetTaipeiNow(), CancellationToken.None);

        Assert.Equal(IntradayRunStatus.Succeeded, summary.Status);
        Assert.Equal(BaselineDate, summary.BaselineTradeDate);
        Assert.Equal(1, preparation.EnsureCallCount);
        Assert.Equal(1, fixture.Excel.ReadCallCount);
        Assert.Single(summary.Alerts, x => x.AlertKind == AlertKind.MovingAverageTriggered);

        var run = Assert.Single(fixture.StateRepository.SavedRuns);
        Assert.Equal(BaselineDate, run.BaselineTradeDate);
        Assert.Equal(1, run.HoldingCount);
    }

    [Fact]
    public async Task 基準已完整時_直接走快速路徑_不呼叫基準準備也不回補()
    {
        var preparation = new FakeBaselinePreparationService();
        var fixture = CreateFixture(baselinePreparationService: preparation);
        fixture.Excel.Holdings = [CreateHolding("2330", "台積電", entryAveragePrice: 880m, currentPrice: 840m, excelRow: 4)];

        var summary = await fixture.Service.RunOnceAsync(false, fixture.Clock.GetTaipeiNow(), CancellationToken.None);

        Assert.Equal(IntradayRunStatus.Succeeded, summary.Status);
        Assert.Equal(0, preparation.EnsureCallCount);
        Assert.Equal(1, fixture.Excel.ReadCallCount);
        Assert.Contains("本輪只重新讀取客戶價格", summary.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task 基準準備失敗時_不得進入價格判斷()
    {
        var preparation = new FakeBaselinePreparationService
        {
            Result = new BaselinePreparationResult(
                new BaselinePreparationState(
                    EvaluationDate, BaselineDate, true, true, false,
                    BaselinePreparationStatus.Partial, null,
                    "均價快照不完整。", 2, 1),
                PreparedThisRun: true,
                IsAnotherPreparationRunning: false,
                "均價快照不完整。")
        };
        var fixture = CreateFixture(baselinePreparationService: preparation);
        fixture.Resolver.Resolution = new IntradayBaselineResolution(
            EvaluationDate, null, false,
            "上一交易日均價快照不存在。",
            BaselineDate, null, BaselineDate);

        var summary = await fixture.Service.RunOnceAsync(false, fixture.Clock.GetTaipeiNow(), CancellationToken.None);

        Assert.Equal(IntradayRunStatus.BaselineNotReady, summary.Status);
        Assert.Equal(1, preparation.EnsureCallCount);
        Assert.Equal(0, fixture.Excel.ReadCallCount);
        Assert.Empty(summary.Alerts);
    }

    [Fact]
    public async Task 其他盤中Tick遇到基準準備中_直接略過且不讀Excel()
    {
        var preparation = new FakeBaselinePreparationService
        {
            Result = new BaselinePreparationResult(
                new BaselinePreparationState(
                    EvaluationDate, BaselineDate, true, false, false,
                    BaselinePreparationStatus.Backfilling, null,
                    "另一個盤中流程正在準備上一交易日資料。", 0, 0),
                PreparedThisRun: false,
                IsAnotherPreparationRunning: true,
                "盤中監控：正在準備上一交易日資料，本次 Tick 略過，不排隊。")
        };
        var fixture = CreateFixture(baselinePreparationService: preparation);
        fixture.Resolver.Resolution = new IntradayBaselineResolution(
            EvaluationDate, null, false,
            "上一交易日收盤價缺漏。",
            BaselineDate, null, BaselineDate);

        var summary = await fixture.Service.RunOnceAsync(false, fixture.Clock.GetTaipeiNow(), CancellationToken.None);

        Assert.Equal(IntradayRunStatus.BaselineNotReady, summary.Status);
        Assert.Contains("正在準備上一交易日資料", summary.Message, StringComparison.Ordinal);
        Assert.Equal(0, fixture.Excel.ReadCallCount);
    }

    [Fact]
    public async Task 執行鎖被收盤更新持有時_盤中Tick直接記錄略過不排隊()
    {
        var fixture = CreateFixture();
        fixture.Excel.Holdings = [CreateHolding("2330", "台積電", entryAveragePrice: 880m, currentPrice: 840m, excelRow: 4)];

        using (await fixture.Gate.EnterAsync("收盤更新", CancellationToken.None))
        {
            var summary = await fixture.Service.RunOnceAsync(false, fixture.Clock.GetTaipeiNow(), CancellationToken.None);
            Assert.Equal(IntradayRunStatus.Skipped, summary.Status);
            Assert.Equal(0, fixture.Excel.ReadCallCount);
            var run = Assert.Single(fixture.StateRepository.SavedRuns);
            Assert.Equal(IntradayRunStatus.Skipped, run.Status);
            Assert.Contains("收盤更新", run.SkippedReason!, StringComparison.Ordinal);
        }

        // 鎖釋放後下一次 Tick 照常執行。
        fixture.Clock.Advance(TimeSpan.FromMinutes(1));
        var next = await fixture.Service.RunOnceAsync(false, fixture.Clock.GetTaipeiNow(), CancellationToken.None);
        Assert.Equal(IntradayRunStatus.Succeeded, next.Status);
    }

    [Fact]
    public async Task Excel活頁簿完全無法存取時_該次Tick失敗並記錄原因_下一次Tick不受影響()
    {
        var fixture = CreateFixture();
        fixture.Excel.ThrowOnRead = new InvalidOperationException("Excel 活頁簿目前無法存取。");

        var failed = await fixture.Service.RunOnceAsync(false, fixture.Clock.GetTaipeiNow(), CancellationToken.None);
        Assert.Equal(IntradayRunStatus.Failed, failed.Status);
        Assert.Contains("Excel 活頁簿目前無法存取", failed.Message, StringComparison.Ordinal);

        // 修復後下一分鐘照常成功；失敗不影響去重狀態的正確性。
        fixture.Clock.Advance(TimeSpan.FromMinutes(1));
        fixture.Excel.ThrowOnRead = null;
        fixture.Excel.Holdings = [CreateHolding("2330", "台積電", entryAveragePrice: 880m, currentPrice: 840m, excelRow: 4)];
        var next = await fixture.Service.RunOnceAsync(false, fixture.Clock.GetTaipeiNow(), CancellationToken.None);
        Assert.Equal(IntradayRunStatus.Succeeded, next.Status);
        Assert.Equal(1, next.NewNotificationCount);
    }

    private static CustomerHolding CreateHolding(
        string code, string name, decimal? entryAveragePrice, decimal? currentPrice,
        string? currentPriceIssue = null, string? entryAveragePriceIssue = null, int excelRow = 4) => new(
        EvaluationDate,
        WorkbookPath,
        "客戶頁籤",
        "測試客戶",
        excelRow,
        code,
        name,
        currentPrice,
        10,
        $"key-{code}-{excelRow}",
        currentPriceIssue,
        entryAveragePrice,
        entryAveragePriceIssue);

    /// <summary>模擬舊版本（依單一 MA 天數分開去重）當日殘留的 MaWindow=5／20／120 Active 狀態，
    /// 對應 2330／列 4 這一持股，用於驗證第一次執行新版時會被清除、不阻擋新的 MaWindow=0 複合通知。</summary>
    private static IntradayAlertStateRecord LegacyMovingAverageState(int maWindow, DateTimeOffset now) => new(
        EvaluationDate,
        BaselineDate,
        WorkbookPath,
        "客戶頁籤",
        4,
        "2330",
        AlertKind.MovingAverageTriggered,
        maWindow,
        IsActive: true,
        FirstTriggeredAt: now,
        LastEvaluatedAt: now,
        LastNotifiedAt: now,
        ClearedAt: null);

    private static Fixture CreateFixture(
        InMemoryIntradayStateRepository? stateRepository = null,
        MutableClock? clock = null,
        IBaselinePreparationService? baselinePreparationService = null)
    {
        clock ??= new MutableClock(new DateTimeOffset(EvaluationDate.ToDateTime(new TimeOnly(10, 31)), TimeSpan.FromHours(8)));
        stateRepository ??= new InMemoryIntradayStateRepository();

        var settings = AppSettings.CreateDefault();
        settings.WorkbookPath = WorkbookPath;

        var baselineMovingAverages = new Dictionary<string, MovingAverageResult>(StringComparer.OrdinalIgnoreCase)
        {
            // 上一交易日（2026-07-13）已保存的均價快照：MA5=860／MA20=870／MA60=880／MA120=890。
            ["2330"] = new("2330", BaselineDate, 900m, 860m, 870m, 880m, 890m, 120, CalculationStatus.Ok, BaselineDate),
            ["5351"] = new("5351", BaselineDate, 100m, 95m, 90m, 85m, 80m, 120, CalculationStatus.Ok, BaselineDate)
        };

        var marketData = new SpyMarketDataRepository(baselineMovingAverages);
        var excel = new SpyExcelWorkbookService();
        var resolver = new FakeTradingDateResolver
        {
            Resolution = new IntradayBaselineResolution(EvaluationDate, BaselineDate, true, null, BaselineDate, BaselineDate)
        };
        var gate = new WorkflowExecutionGate();

        var userInteraction = new FakeUserInteraction();
        var service = new IntradayMonitoringService(
            clock,
            new FakeSettingsStore(settings),
            resolver,
            marketData,
            excel,
            new StockIdentityResolutionService(marketData),
            new StrategyEvaluationService(),
            stateRepository,
            gate,
            new FakeLogger(),
            baselinePreparationService,
            userInteraction);

        return new Fixture(service, clock, excel, marketData, resolver, stateRepository, gate, userInteraction);
    }

    private sealed record Fixture(
        IntradayMonitoringService Service,
        MutableClock Clock,
        SpyExcelWorkbookService Excel,
        SpyMarketDataRepository MarketData,
        FakeTradingDateResolver Resolver,
        InMemoryIntradayStateRepository StateRepository,
        WorkflowExecutionGate Gate,
        FakeUserInteraction UserInteraction);

    private sealed class MutableClock(DateTimeOffset now) : IClock
    {
        private DateTimeOffset _now = now;
        public void Advance(TimeSpan delta) => _now += delta;
        public DateTimeOffset GetTaipeiNow() => _now;
        public DateOnly GetTaipeiToday() => DateOnly.FromDateTime(_now.DateTime);
    }

    private sealed class FakeLogger : IAppLogger
    {
        public void Info(string message) { }
        public void Warning(string message) { }
        public void Error(string message, Exception? exception = null) { }
    }

    private sealed class FakeSettingsStore(AppSettings settings) : ISettingsStore
    {
        public Task<AppSettings> LoadAsync(CancellationToken cancellationToken = default) => Task.FromResult(settings);
        public Task SaveAsync(AppSettings settings, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

    private sealed class FakeTradingDateResolver : ITradingDateResolver
    {
        public IntradayBaselineResolution Resolution { get; set; } = null!;
        public Queue<IntradayBaselineResolution> Resolutions { get; } = new();

        public Task<IntradayBaselineResolution> ResolveBaselineAsync(DateOnly evaluationDate, CancellationToken cancellationToken)
            => Task.FromResult(Resolutions.Count > 0 ? Resolutions.Dequeue() : Resolution);

        public Task<bool> IsKnownNonTradingDayAsync(DateOnly date, CancellationToken cancellationToken)
            => Task.FromResult(date.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday);
    }

    private sealed class FakeUserInteraction : IUserInteraction
    {
        public List<(string Message, int Percent)> Statuses { get; } = [];
        public List<string> ProgressDetails { get; } = [];

        public Task<bool> ConfirmExcelSafetyAsync(CancellationToken cancellationToken) => Task.FromResult(true);
        public void ShowStatus(string message, int percentComplete = 0) => Statuses.Add((message, percentComplete));
        public void ShowProgressDetail(string message) => ProgressDetails.Add(message);
        public void ShowSuccess(JobRunSummary summary) { }
        public void ShowFailure(JobRunSummary summary) { }
    }

    private sealed class FakeBaselinePreparationService : IBaselinePreparationService
    {
        public int EnsureCallCount { get; private set; }

        public BaselinePreparationResult Result { get; set; } = new(
            new BaselinePreparationState(
                EvaluationDate, BaselineDate, true, true, true,
                BaselinePreparationStatus.Ready, null, null, 2, 2),
            PreparedThisRun: true,
            IsAnotherPreparationRunning: false,
            "基準資料已準備完成。");

        public Task<BaselinePreparationState> GetStateAsync(DateOnly evaluationDate, DateOnly? baselineTradeDate, CancellationToken cancellationToken)
            => Task.FromResult(new BaselinePreparationState(
                evaluationDate, baselineTradeDate, baselineTradeDate is not null,
                true, true, BaselinePreparationStatus.Ready, null, null, 2, 2));

        public Task<BaselinePreparationResult> EnsureBaselineDataAsync(
            DateOnly evaluationDate,
            IntradayBaselineResolution initialResolution,
            OfficialMarketDataSettings settings,
            CancellationToken cancellationToken)
        {
            EnsureCallCount++;
            return Task.FromResult(Result);
        }
    }

    /// <summary>盤中 Tick 只允許讀取客戶持股；WriteStrategyResultsAsync 被呼叫就直接失敗。</summary>
    private sealed class SpyExcelWorkbookService : IExcelWorkbookService
    {
        public IReadOnlyList<CustomerHolding> Holdings { get; set; } = [];
        public Exception? ThrowOnRead { get; set; }
        public int ReadCallCount { get; private set; }
        public int WriteCallCount { get; private set; }

        public Task<IReadOnlyList<CustomerHolding>> ReadHoldingsAsync(
            AppSettings settings, DateOnly targetDate, CancellationToken cancellationToken, Action<string>? reportProgress = null)
        {
            ReadCallCount++;
            reportProgress?.Invoke("正在掃描 Excel 客戶頁籤。");
            if (ThrowOnRead is not null)
            {
                throw ThrowOnRead;
            }

            return Task.FromResult(Holdings);
        }

        public Task WriteStrategyResultsAsync(
            AppSettings settings, DateOnly targetDate, IReadOnlyList<DailyMovingAverageSnapshot> rows, CancellationToken cancellationToken)
        {
            WriteCallCount++;
            throw new InvalidOperationException("盤中流程不得寫入「每日五日均價策略」頁籤。");
        }
    }

    /// <summary>只提供上一交易日均價快照的讀取；任何寫入或均線計算相關呼叫都以計數器揭露。</summary>
    private sealed class SpyMarketDataRepository(Dictionary<string, MovingAverageResult> baselineMovingAverages) : IMarketDataRepository
    {
        public List<DateOnly> RequestedMovingAverageDates { get; } = [];
        public int SaveMovingAverageCallCount { get; private set; }

        public Task InitializeAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task<string> BeginPriceBatchAsync(OfficialPriceJobType jobType, DateOnly targetDate, string sourceProvider, MarketType marketType, DateTimeOffset startedAt, CancellationToken cancellationToken)
            => throw new InvalidOperationException("盤中流程不得建立官方價格批次。");

        public Task CompletePriceBatchAsync(OfficialPriceBatchSummary summary, CancellationToken cancellationToken)
            => throw new InvalidOperationException("盤中流程不得寫入官方價格批次。");

        public Task<(int Inserted, int Updated)> UpsertDailyPricesAsync(IReadOnlyList<OfficialStockPrice> prices, CancellationToken cancellationToken)
            => throw new InvalidOperationException("盤中流程不得寫入 StockDailyPrice。");

        public Task<IReadOnlyList<(DateOnly TradeDate, decimal ClosePrice)>> GetRecentClosePricesAsync(string stockCode, DateOnly upToDate, int maxTradingDays, CancellationToken cancellationToken)
            => Task.FromResult<IReadOnlyList<(DateOnly, decimal)>>([]);

        public Task<MarketType?> GetStockMarketTypeAsync(string stockCode, CancellationToken cancellationToken)
            => Task.FromResult(baselineMovingAverages.ContainsKey(stockCode) ? (MarketType?)MarketType.Listed : null);

        public Task<IReadOnlyDictionary<string, MarketType>> GetStockMarketTypesAsync(IReadOnlyCollection<string> stockCodes, CancellationToken cancellationToken)
            => Task.FromResult<IReadOnlyDictionary<string, MarketType>>(
                stockCodes.Where(baselineMovingAverages.ContainsKey)
                    .ToDictionary(code => code, _ => MarketType.Listed, StringComparer.OrdinalIgnoreCase));

        public Task<IReadOnlyList<string>> GetStockCodesWithDailyPriceAsync(DateOnly tradeDate, CancellationToken cancellationToken)
            => Task.FromResult<IReadOnlyList<string>>([]);

        public Task SaveMovingAverageResultsAsync(DateOnly tradeDate, IReadOnlyList<MovingAverageResult> results, CancellationToken cancellationToken)
        {
            SaveMovingAverageCallCount++;
            throw new InvalidOperationException("盤中流程不得寫入或覆蓋 StockMovingAverage。");
        }

        public Task<IReadOnlyList<MovingAverageResult>> GetMovingAverageResultsAsync(DateOnly tradeDate, CancellationToken cancellationToken)
        {
            RequestedMovingAverageDates.Add(tradeDate);
            IReadOnlyList<MovingAverageResult> results = tradeDate == BaselineDate
                ? baselineMovingAverages.Values.ToArray()
                : [];
            return Task.FromResult(results);
        }

        public Task<IReadOnlyList<DailyMovingAverageSnapshot>> GetMovingAverageSnapshotsAsync(DateOnly tradeDate, CancellationToken cancellationToken)
            => Task.FromResult<IReadOnlyList<DailyMovingAverageSnapshot>>([]);

        public Task<IReadOnlyList<DailyMovingAverageSnapshot>> GetMovingAverageAnomaliesAsync(DateOnly tradeDate, CancellationToken cancellationToken)
            => Task.FromResult<IReadOnlyList<DailyMovingAverageSnapshot>>([]);

        public Task<int> GetDistinctTradeDateCountAsync(DateOnly upToDate, int maxTradingDays, CancellationToken cancellationToken)
            => Task.FromResult(0);

        public Task<int> GetDistinctTradeDateCountAsync(DateOnly upToDate, int maxTradingDays, MarketType marketType, CancellationToken cancellationToken)
            => Task.FromResult(0);

        public Task<int> GetDistinctTradeDateCountAsync(DateOnly upToDate, int maxTradingDays, string stockCode, CancellationToken cancellationToken)
            => Task.FromResult(0);

        public Task<bool> HasDailyPricesAsync(DateOnly tradeDate, MarketType marketType, CancellationToken cancellationToken)
            => Task.FromResult(true);

        public Task<bool> HasDailyPriceAsync(DateOnly tradeDate, string stockCode, CancellationToken cancellationToken)
            => Task.FromResult(true);

        public Task<bool> HasSucceededBatchAsync(OfficialPriceJobType jobType, DateOnly targetDate, string sourceProvider, CancellationToken cancellationToken)
            => Task.FromResult(false);

        public Task<bool> HasResolvedHolidayBatchAsync(DateOnly targetDate, string sourceProvider, CancellationToken cancellationToken)
            => Task.FromResult(false);

        public Task<StockDailyPriceQueryResult> QueryDailyPricesAsync(StockDailyPriceQueryFilter filter, CancellationToken cancellationToken)
            => Task.FromResult(new StockDailyPriceQueryResult([], 0, filter.Page, filter.PageSize));

        public Task<DateOnly?> GetLatestTradeDateAsync(CancellationToken cancellationToken) => Task.FromResult<DateOnly?>(null);

        public Task<IReadOnlySet<string>> GetConfirmedNoEmergingDataCodesAsync(DateOnly tradeDate, IReadOnlyCollection<string> stockCodes, CancellationToken cancellationToken)
            => Task.FromResult<IReadOnlySet<string>>(new HashSet<string>(StringComparer.OrdinalIgnoreCase));

        public Task RecordConfirmedNoEmergingDataAsync(DateOnly tradeDate, IReadOnlyCollection<string> stockCodes, DateTimeOffset checkedAt, CancellationToken cancellationToken)
            => Task.CompletedTask;
    }

    /// <summary>以記憶體字典模擬 IntradayAlertState／IntradayEvaluationRun；同一實例跨 Service 使用即可模擬重啟恢復。</summary>
    private sealed class InMemoryIntradayStateRepository : IIntradayStateRepository
    {
        private readonly Dictionary<string, IntradayAlertStateRecord> _states = new(StringComparer.Ordinal);
        public List<IntradayEvaluationRunRecord> SavedRuns { get; } = [];

        public Task InitializeAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task<IReadOnlyList<IntradayAlertStateRecord>> GetAlertStatesAsync(DateOnly evaluationDate, string workbookPath, CancellationToken cancellationToken)
            => Task.FromResult<IReadOnlyList<IntradayAlertStateRecord>>(
                _states.Values
                    .Where(x => x.EvaluationDate == evaluationDate
                                && string.Equals(x.WorkbookPath, workbookPath, StringComparison.OrdinalIgnoreCase))
                    .ToArray());

        public Task UpsertAlertStatesAsync(IReadOnlyList<IntradayAlertStateRecord> states, CancellationToken cancellationToken)
        {
            foreach (var state in states)
            {
                var key = string.Join('|', state.EvaluationDate, state.WorkbookPath, state.SheetName,
                    state.ExcelRow, state.StockCode.ToUpperInvariant(), (int)state.AlertKind, state.MaWindow);
                _states[key] = state;
            }

            return Task.CompletedTask;
        }

        public Task SaveEvaluationRunAsync(IntradayEvaluationRunRecord run, CancellationToken cancellationToken)
        {
            SavedRuns.Add(run);
            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<IntradayEvaluationRunRecord>> GetEvaluationRunsAsync(DateOnly evaluationDate, int limit, CancellationToken cancellationToken)
            => Task.FromResult<IReadOnlyList<IntradayEvaluationRunRecord>>(
                SavedRuns.Where(x => x.EvaluationDate == evaluationDate).Reverse().Take(limit).ToArray());

        public IReadOnlyList<IntradayAlertStateRecord> GetStatesSnapshot() => _states.Values.ToArray();
    }
}
