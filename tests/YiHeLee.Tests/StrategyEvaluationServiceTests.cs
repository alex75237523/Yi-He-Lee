using YiHeLee.Application.Services;
using YiHeLee.Domain;

namespace YiHeLee.Tests;

/// <summary>
/// 2026-07-19 正式策略：唯一通知條件為「進場價/平均價 &gt; MA20 且 現價 &lt; MA5」，必須兩項同時成立才觸發。
/// 邊界一律嚴格大於／小於（相等不觸發）。MA60／MA120 只保存與顯示，不參與觸發。
/// 複合條件同時需要 MA5 與 MA20，任一缺少即無法判斷並明確顯示缺少哪一條，不得改用其他均線替代。
/// 「進場價/平均價」與「現價」是兩個完全獨立的欄位，任一無效都不得觸發，且必須各自產生對應的異常通知。
/// 下列多數案例對應需求「十三、人工驗收」的實機案例一～五。
/// </summary>
public sealed class StrategyEvaluationServiceTests
{
    private static readonly DateOnly TradeDate = new(2026, 7, 9);
    private static readonly DateTimeOffset TaipeiNow = new(2026, 7, 9, 13, 35, 0, TimeSpan.FromHours(8));

    // 案例一：進場價 120 > MA20 115，且現價 98 < MA5 100 → 觸發。
    [Fact]
    public void 進場價高於MA20且現價低於MA5時_觸發()
    {
        var holding = CreateHolding(entryAveragePrice: 120m, currentPrice: 98m);
        var ma = CreateMovingAverage(close: 110m, ma5: 100m, ma20: 115m, ma60: 90m, ma120: 90m);

        var result = new StrategyEvaluationService().Evaluate(
            TradeDate, [holding], [ma], MarketTypesFor(MarketType.Listed), TaipeiNow);

        var alert = Assert.Single(result);
        Assert.Equal(AlertKind.MovingAverageTriggered, alert.AlertKind);
        Assert.True(alert.TriggeredMa20);   // 進場價 > MA20
        Assert.True(alert.TriggeredMa5);    // 現價 < MA5
        Assert.False(alert.TriggeredMa120); // MA120 不參與策略
    }

    // 案例二：進場價 110 !> MA20 115（第一條不成立）→ 不觸發，即使現價 98 < MA5 100。
    [Fact]
    public void 只有現價低於MA5但進場價未高於MA20時_不觸發()
    {
        var holding = CreateHolding(entryAveragePrice: 110m, currentPrice: 98m);
        var ma = CreateMovingAverage(close: 110m, ma5: 100m, ma20: 115m, ma60: 90m, ma120: 90m);

        var result = new StrategyEvaluationService().Evaluate(
            TradeDate, [holding], [ma], MarketTypesFor(MarketType.Listed), TaipeiNow);

        Assert.Empty(result);
    }

    // 案例三：現價 102 !< MA5 100（第二條不成立）→ 不觸發，即使進場價 120 > MA20 115。
    [Fact]
    public void 只有進場價高於MA20但現價未低於MA5時_不觸發()
    {
        var holding = CreateHolding(entryAveragePrice: 120m, currentPrice: 102m);
        var ma = CreateMovingAverage(close: 110m, ma5: 100m, ma20: 115m, ma60: 90m, ma120: 90m);

        var result = new StrategyEvaluationService().Evaluate(
            TradeDate, [holding], [ma], MarketTypesFor(MarketType.Listed), TaipeiNow);

        Assert.Empty(result);
    }

    [Fact]
    public void 兩個子條件都不成立時_不觸發()
    {
        // 進場價 110 !> MA20 115、現價 130 !< MA5 100。
        var holding = CreateHolding(entryAveragePrice: 110m, currentPrice: 130m);
        var ma = CreateMovingAverage(close: 110m, ma5: 100m, ma20: 115m, ma60: 90m, ma120: 90m);

        var result = new StrategyEvaluationService().Evaluate(
            TradeDate, [holding], [ma], MarketTypesFor(MarketType.Listed), TaipeiNow);

        Assert.Empty(result);
    }

    // 案例四：進場價 115 == MA20 115 → 嚴格大於，相等不觸發。
    [Fact]
    public void 進場價等於MA20時_嚴格大於邊界不觸發()
    {
        var holding = CreateHolding(entryAveragePrice: 115m, currentPrice: 98m);
        var ma = CreateMovingAverage(close: 110m, ma5: 100m, ma20: 115m, ma60: 90m, ma120: 90m);

        var result = new StrategyEvaluationService().Evaluate(
            TradeDate, [holding], [ma], MarketTypesFor(MarketType.Listed), TaipeiNow);

        Assert.Empty(result);
    }

    // 案例五：現價 100 == MA5 100 → 嚴格小於，相等不觸發。
    [Fact]
    public void 現價等於MA5時_嚴格小於邊界不觸發()
    {
        var holding = CreateHolding(entryAveragePrice: 120m, currentPrice: 100m);
        var ma = CreateMovingAverage(close: 110m, ma5: 100m, ma20: 115m, ma60: 90m, ma120: 90m);

        var result = new StrategyEvaluationService().Evaluate(
            TradeDate, [holding], [ma], MarketTypesFor(MarketType.Listed), TaipeiNow);

        Assert.Empty(result);
    }

    [Fact]
    public void MA5缺少時_不觸發並明確顯示無法判斷缺少MA5()
    {
        // 即使進場價 120 > MA20 115 成立，MA5 缺少就不得判斷、不得用其他均線替代。
        var holding = CreateHolding(entryAveragePrice: 120m, currentPrice: 98m);
        var ma = new MovingAverageResult("5285", TradeDate, 110m, null, 115m, 90m, 90m, 4, CalculationStatus.InsufficientHistory, TradeDate, "僅累積 4 個有效交易日，MA5 尚缺 1 個有效交易日。");

        var result = new StrategyEvaluationService().Evaluate(
            TradeDate, [holding], [ma], MarketTypesFor(MarketType.Listed), TaipeiNow);

        var alert = Assert.Single(result);
        Assert.Equal(AlertKind.TechnicalIndicatorMissing, alert.AlertKind);
        Assert.False(alert.TriggeredMa5);
        Assert.False(alert.TriggeredMa20);
        Assert.False(alert.TriggeredMa120);
        Assert.Contains("MA5", alert.TriggerDescription, StringComparison.Ordinal);
        Assert.Equal("均線資料不足", alert.DiagnosticStatus);
    }

    [Fact]
    public void MA20缺少時_不觸發並明確顯示無法判斷缺少MA20()
    {
        // 即使現價 98 < MA5 100 成立，MA20 缺少就不得判斷、不得用其他均線替代。
        var holding = CreateHolding(entryAveragePrice: 120m, currentPrice: 98m);
        var ma = new MovingAverageResult("5285", TradeDate, 110m, 100m, null, 90m, 90m, 12, CalculationStatus.InsufficientHistory, TradeDate, "僅累積 12 個有效交易日，MA20 尚缺 8 個有效交易日。");

        var result = new StrategyEvaluationService().Evaluate(
            TradeDate, [holding], [ma], MarketTypesFor(MarketType.Listed), TaipeiNow);

        var alert = Assert.Single(result);
        Assert.Equal(AlertKind.TechnicalIndicatorMissing, alert.AlertKind);
        Assert.False(alert.TriggeredMa5);
        Assert.False(alert.TriggeredMa20);
        Assert.Contains("MA20", alert.TriggerDescription, StringComparison.Ordinal);
    }

    [Fact]
    public void MA60無論數值為何_都不影響觸發結果()
    {
        // 進場價 120 > MA20 115、現價 98 < MA5 100 → 觸發；MA60 極端值不影響。
        var holdingHighMa60 = CreateHolding(entryAveragePrice: 120m, currentPrice: 98m);
        var maHighMa60 = CreateMovingAverage(close: 110m, ma5: 100m, ma20: 115m, ma60: 99999m, ma120: 90m);
        var maLowMa60 = CreateMovingAverage(close: 110m, ma5: 100m, ma20: 115m, ma60: 1m, ma120: 90m);

        var service = new StrategyEvaluationService();
        var high = service.Evaluate(TradeDate, [holdingHighMa60], [maHighMa60], MarketTypesFor(MarketType.Listed), TaipeiNow);
        var low = service.Evaluate(TradeDate, [holdingHighMa60], [maLowMa60], MarketTypesFor(MarketType.Listed), TaipeiNow);

        Assert.Single(high);
        Assert.Single(low);
        Assert.Equal(AlertKind.MovingAverageTriggered, high[0].AlertKind);
        Assert.Equal(AlertKind.MovingAverageTriggered, low[0].AlertKind);
    }

    [Fact]
    public void MA120無論數值為何_都不影響觸發結果且TriggeredMa120固定為false()
    {
        var holding = CreateHolding(entryAveragePrice: 120m, currentPrice: 98m);
        var maHighMa120 = CreateMovingAverage(close: 110m, ma5: 100m, ma20: 115m, ma60: 90m, ma120: 99999m);
        var maLowMa120 = CreateMovingAverage(close: 110m, ma5: 100m, ma20: 115m, ma60: 90m, ma120: 1m);

        var service = new StrategyEvaluationService();
        var high = Assert.Single(service.Evaluate(TradeDate, [holding], [maHighMa120], MarketTypesFor(MarketType.Listed), TaipeiNow));
        var low = Assert.Single(service.Evaluate(TradeDate, [holding], [maLowMa120], MarketTypesFor(MarketType.Listed), TaipeiNow));

        Assert.Equal(AlertKind.MovingAverageTriggered, high.AlertKind);
        Assert.Equal(AlertKind.MovingAverageTriggered, low.AlertKind);
        Assert.False(high.TriggeredMa120);
        Assert.False(low.TriggeredMa120);
    }

    [Fact]
    public void 舊OR規則會觸發的案例_新複合條件不得再觸發()
    {
        // 舊規則：MA5／MA20／MA120 任一 >= 進場價或現價即觸發。此處三條均價皆高於兩個價格，舊規則會觸發。
        // 新規則：進場價 100 !> MA20 121 → 不觸發。
        var holding = CreateHolding(entryAveragePrice: 100m, currentPrice: 100m);
        var ma = CreateMovingAverage(close: 130m, ma5: 120m, ma20: 121m, ma60: 122m, ma120: 123m);

        var result = new StrategyEvaluationService().Evaluate(
            TradeDate, [holding], [ma], MarketTypesFor(MarketType.Listed), TaipeiNow);

        Assert.Empty(result);
    }

    [Fact]
    public void 觸發通知說明列出兩個子條件與實際數值()
    {
        var holding = CreateHolding(entryAveragePrice: 120m, currentPrice: 98m);
        var ma = CreateMovingAverage(close: 110m, ma5: 100m, ma20: 115m, ma60: 90m, ma120: 90m);

        var alert = Assert.Single(new StrategyEvaluationService().Evaluate(
            TradeDate, [holding], [ma], MarketTypesFor(MarketType.Listed), TaipeiNow));

        Assert.Contains("進場價/平均價 120", alert.TriggerDescription, StringComparison.Ordinal);
        Assert.Contains("MA20 115", alert.TriggerDescription, StringComparison.Ordinal);
        Assert.Contains("現價 98", alert.TriggerDescription, StringComparison.Ordinal);
        Assert.Contains("MA5 100", alert.TriggerDescription, StringComparison.Ordinal);
        Assert.Contains(">", alert.TriggerDescription, StringComparison.Ordinal);
        Assert.Contains("<", alert.TriggerDescription, StringComparison.Ordinal);
    }

    [Fact]
    public void 進場價空白時_產生進場價平均價異常且不觸發()
    {
        var holding = CreateHolding(entryAveragePrice: null, currentPrice: 98m, entryAveragePriceIssue: "儲存格為空白，無法讀取進場價/平均價");
        var ma = CreateMovingAverage(close: 110m, ma5: 100m, ma20: 115m, ma60: 90m, ma120: 90m);

        var result = new StrategyEvaluationService().Evaluate(
            TradeDate, [holding], [ma], MarketTypesFor(MarketType.Listed), TaipeiNow);

        var alert = Assert.Single(result);
        Assert.Equal(AlertKind.EntryAveragePriceInvalid, alert.AlertKind);
        Assert.Null(alert.EntryAveragePrice);
        Assert.False(alert.TriggeredMa5);
        Assert.False(alert.TriggeredMa20);
        Assert.False(alert.TriggeredMa120);
        Assert.Contains("空白", alert.TriggerDescription, StringComparison.Ordinal);
        Assert.DoesNotContain("DDE", alert.TriggerDescription, StringComparison.Ordinal);
    }

    [Fact]
    public void 進場價為0時_產生進場價平均價異常()
    {
        var holding = CreateHolding(entryAveragePrice: null, currentPrice: 100m, entryAveragePriceIssue: "數值為 0（非正數），非有效的進場價/平均價");

        var result = new StrategyEvaluationService().Evaluate(
            TradeDate, [holding], [], MarketTypesFor(MarketType.Listed), TaipeiNow);

        var alert = Assert.Single(result);
        Assert.Equal(AlertKind.EntryAveragePriceInvalid, alert.AlertKind);
        Assert.Equal("進場價/平均價異常", alert.DiagnosticStatus);
    }

    [Fact]
    public void 進場價為Excel錯誤值或文字時_產生進場價平均價異常()
    {
        var holding = CreateHolding(entryAveragePrice: null, currentPrice: 100m, entryAveragePriceIssue: "儲存格為 #N/A（找不到對應資料），非有效的進場價/平均價（非 DDE 欄位）");

        var result = new StrategyEvaluationService().Evaluate(
            TradeDate, [holding], [], MarketTypesFor(MarketType.Listed), TaipeiNow);

        var alert = Assert.Single(result);
        Assert.Equal(AlertKind.EntryAveragePriceInvalid, alert.AlertKind);
        Assert.Contains("#N/A", alert.TriggerDescription, StringComparison.Ordinal);
    }

    [Fact]
    public void 現價DDE無效時_仍維持現價異常()
    {
        var holding = CreateHolding(entryAveragePrice: 120m, currentPrice: null, currentPriceIssue: "儲存格為 #N/A（DDE 尚未取得資料，看盤軟體可能未開啟或未連線）");
        var ma = CreateMovingAverage(close: 110m, ma5: 100m, ma20: 115m, ma60: 90m, ma120: 90m);

        var result = new StrategyEvaluationService().Evaluate(
            TradeDate, [holding], [ma], MarketTypesFor(MarketType.Listed), TaipeiNow);

        var alert = Assert.Single(result);
        Assert.Equal(AlertKind.CurrentPriceInvalid, alert.AlertKind);
        Assert.Null(alert.CurrentPrice);
        Assert.Equal(120m, alert.EntryAveragePrice);
        Assert.Contains("#N/A", alert.TriggerDescription, StringComparison.Ordinal);
        Assert.Contains("DDE", alert.TriggerDescription, StringComparison.Ordinal);

        // MA5／MA20／MA60／MA120 是依官方收盤價自行計算，與現價（DDE）無關；即使現價異常仍必須照實顯示。
        Assert.Equal(110m, alert.ClosePrice);
        Assert.Equal(100m, alert.MovingAverage5);
    }

    [Fact]
    public void 兩個價格皆無效時_兩個異常原因都不會遺失()
    {
        var holding = CreateHolding(
            entryAveragePrice: null,
            currentPrice: null,
            entryAveragePriceIssue: "儲存格為空白，無法讀取進場價/平均價",
            currentPriceIssue: "儲存格為 #N/A（DDE 尚未取得資料，看盤軟體可能未開啟或未連線）");

        var result = new StrategyEvaluationService().Evaluate(
            TradeDate, [holding], [], MarketTypesFor(MarketType.Listed), TaipeiNow);

        Assert.Equal(2, result.Count);
        var entryAlert = Assert.Single(result, x => x.AlertKind == AlertKind.EntryAveragePriceInvalid);
        var currentAlert = Assert.Single(result, x => x.AlertKind == AlertKind.CurrentPriceInvalid);
        Assert.Contains("空白", entryAlert.TriggerDescription, StringComparison.Ordinal);
        Assert.Contains("#N/A", currentAlert.TriggerDescription, StringComparison.Ordinal);
        Assert.NotEqual(entryAlert.TriggerDescription, currentAlert.TriggerDescription);
    }

    [Fact]
    public void 不得以收盤價代替進場價或現價()
    {
        // 收盤價 116 > MA20 115，但進場價 100 !> MA20 115：不得誤用收盤價判斷觸發。
        var holding = CreateHolding(entryAveragePrice: 100m, currentPrice: 98m);
        var ma = CreateMovingAverage(close: 116m, ma5: 100m, ma20: 115m, ma60: 90m, ma120: 90m);

        var result = new StrategyEvaluationService().Evaluate(
            TradeDate, [holding], [ma], MarketTypesFor(MarketType.Listed), TaipeiNow);

        Assert.Empty(result);
    }

    [Fact]
    public void EvaluateAll與Evaluate的複合判斷完全一致()
    {
        var holdings = new[]
        {
            // 觸發：進場價 120 > MA20 115、現價 98 < MA5 100。
            CreateHolding(entryAveragePrice: 120m, currentPrice: 98m, code: "5285", name: "宜鼎"),
            // 未觸發：進場價 110 !> MA20 115。
            CreateHolding(entryAveragePrice: 110m, currentPrice: 98m, code: "2330", name: "台積電"),
            // 進場價無效。
            CreateHolding(entryAveragePrice: null, currentPrice: 98m, code: "3691", name: "碩禾", entryAveragePriceIssue: "儲存格為空白，無法讀取進場價/平均價"),
        };
        var mas = new[]
        {
            CreateMovingAverage(close: 110m, ma5: 100m, ma20: 115m, ma60: 90m, ma120: 90m) with { StockCode = "5285" },
            CreateMovingAverage(close: 110m, ma5: 100m, ma20: 115m, ma60: 90m, ma120: 90m) with { StockCode = "2330" },
            CreateMovingAverage(close: 110m, ma5: 100m, ma20: 115m, ma60: 90m, ma120: 90m) with { StockCode = "3691" },
        };
        var marketTypes = new Dictionary<string, MarketType>(StringComparer.OrdinalIgnoreCase)
        {
            ["5285"] = MarketType.Listed,
            ["2330"] = MarketType.Listed,
            ["3691"] = MarketType.Otc
        };

        var service = new StrategyEvaluationService();
        var alerts = service.Evaluate(TradeDate, holdings, mas, marketTypes, TaipeiNow);
        var allResults = service.EvaluateAll(TradeDate, holdings, mas, marketTypes, TaipeiNow);

        Assert.Equal(3, allResults.Count);

        var triggeredAlert = Assert.Single(alerts, x => x.StockCode == "5285");
        var triggeredResult = Assert.Single(allResults, x => x.RawStockCode == "5285");
        Assert.Equal(triggeredAlert.TriggeredMa5, triggeredResult.Ma5Match);
        Assert.Equal(triggeredAlert.TriggeredMa20, triggeredResult.Ma20Match);
        Assert.Equal(triggeredAlert.TriggeredMa120, triggeredResult.Ma120Match);
        Assert.Equal("觸發", triggeredResult.OverallResult);

        var notTriggeredResult = Assert.Single(allResults, x => x.RawStockCode == "2330");
        Assert.DoesNotContain(alerts, x => x.StockCode == "2330");
        Assert.Equal("未觸發", notTriggeredResult.OverallResult);
        Assert.False(notTriggeredResult.Ma20Match);            // 進場價 110 !> MA20 115
        Assert.True(notTriggeredResult.Ma5Match);              // 現價 98 < MA5 100
        Assert.Contains("必須兩項同時成立", notTriggeredResult.TriggerDescription, StringComparison.Ordinal);

        var invalidEntryAlert = Assert.Single(alerts, x => x.StockCode == "3691");
        Assert.Equal(AlertKind.EntryAveragePriceInvalid, invalidEntryAlert.AlertKind);
        var invalidEntryResult = Assert.Single(allResults, x => x.RawStockCode == "3691");
        Assert.Equal("無效", invalidEntryResult.EntryAveragePriceStatus);
        Assert.Equal("進場價/平均價無效，暫時無法判斷", invalidEntryResult.OverallResult);
    }

    [Fact]
    public void EvaluateAll在MA20缺少時_整體結果為無法判斷並照實輸出已算出的均線()
    {
        var holding = CreateHolding(entryAveragePrice: 120m, currentPrice: 98m);
        var ma = new MovingAverageResult("5285", TradeDate, 110m, 100m, null, 90m, null, 12, CalculationStatus.InsufficientHistory, TradeDate, "僅累積 12 個有效交易日。");

        var result = Assert.Single(new StrategyEvaluationService().EvaluateAll(
            TradeDate, [holding], [ma], MarketTypesFor(MarketType.Listed), TaipeiNow));

        Assert.Equal("無法判斷", result.OverallResult);
        Assert.False(result.Ma5Match);
        Assert.False(result.Ma20Match);
        Assert.False(result.Ma120Match);
        Assert.Contains("MA20", result.TriggerDescription, StringComparison.Ordinal);
        Assert.Equal(100m, result.MovingAverage5);   // 已算出的 MA5 仍照實輸出，不得留白
        Assert.Equal(110m, result.ClosePrice);
    }

    [Fact]
    public void 找不到官方收盤價_列入無法判斷清單()
    {
        var result = new StrategyEvaluationService().Evaluate(
            TradeDate,
            [CreateHolding(entryAveragePrice: 100m, currentPrice: 100m)],
            [],
            MarketTypesFor(MarketType.Listed),
            TaipeiNow);

        var alert = Assert.Single(result);
        Assert.Equal(AlertKind.TechnicalIndicatorMissing, alert.AlertKind);
    }

    [Fact]
    public void 上櫃股票資料來源標示為TPEx()
    {
        // 進場價 130 > MA20 121、現價 119 < MA5 120 → 觸發，藉此驗證上櫃來源標示。
        var ma = CreateMovingAverage(close: 118m, ma5: 120m, ma20: 121m, ma60: 62m, ma120: 100m);

        var result = new StrategyEvaluationService().Evaluate(
            TradeDate,
            [CreateHolding(entryAveragePrice: 130m, currentPrice: 119m)],
            [ma],
            MarketTypesFor(MarketType.Otc),
            TaipeiNow);

        Assert.Equal("TPEx", Assert.Single(result).PriceSourceProvider);
    }

    [Fact]
    public void 股票代碼無法識別時_列入無法判斷清單並標示原因()
    {
        var holding = CreateHolding(entryAveragePrice: 100m, currentPrice: 100m, code: "10037677", name: "金額誤判");
        var resolutions = new Dictionary<string, StockCodeResolution>(StringComparer.OrdinalIgnoreCase)
        {
            ["10037677"] = new StockCodeResolution(
                "10037677", "10037677", null,
                StockIdentityResolver.Resolve("10037677"), false, "股票代碼格式不符合已知台股商品格式，可能為金額或其他非股票資料。")
        };

        var result = new StrategyEvaluationService().Evaluate(
            TradeDate, [holding], [], MarketTypesFor(MarketType.Listed), TaipeiNow, resolutions);

        var alert = Assert.Single(result);
        Assert.Equal(AlertKind.TechnicalIndicatorMissing, alert.AlertKind);
        Assert.Equal("股票代碼無法識別", alert.DiagnosticStatus);
        Assert.False(alert.TriggeredMa5);
        Assert.False(alert.TriggeredMa20);
        Assert.False(alert.TriggeredMa120);
    }

    [Fact]
    public void 權證代碼明確排除於均線策略之外並標示原因()
    {
        var holding = CreateHolding(entryAveragePrice: 5m, currentPrice: 5m, code: "070001", name: "測試權證");
        var resolutions = new Dictionary<string, StockCodeResolution>(StringComparer.OrdinalIgnoreCase)
        {
            ["070001"] = new StockCodeResolution(
                "070001", "070001", null,
                StockIdentityResolver.Resolve("070001"), true,
                "權證為短期衍生性商品，價格行為與長期均線策略無關，依需求明確排除於均線策略之外，非程式錯誤。")
        };

        var result = new StrategyEvaluationService().Evaluate(
            TradeDate, [holding], [], MarketTypesFor(MarketType.Listed), TaipeiNow, resolutions);

        var alert = Assert.Single(result);
        Assert.Equal("非策略商品", alert.DiagnosticStatus);
        Assert.Contains("權證", alert.MissingReason, StringComparison.Ordinal);
    }

    [Fact]
    public void 均線觸發時附上診斷欄位_計算狀態為正常()
    {
        var holding = CreateHolding(entryAveragePrice: 120m, currentPrice: 98m);
        var ma = CreateMovingAverage(close: 110m, ma5: 100m, ma20: 115m, ma60: 90m, ma120: 90m);

        var result = new StrategyEvaluationService().Evaluate(
            TradeDate, [holding], [ma], MarketTypesFor(MarketType.Listed), TaipeiNow);

        var alert = Assert.Single(result);
        Assert.Equal("正常", alert.DiagnosticStatus);
        Assert.Equal(120, alert.AvailableTradingDayCount);
    }

    [Fact]
    public void MA5與MA20已足但缺MA120時_複合條件成立仍通知並標示歷史資料不足()
    {
        // MA5／MA20 已算出、MA120 因逐檔資料不足為 null；進場價 120 > MA20 115、現價 98 < MA5 100 → 仍觸發。
        var ma = new MovingAverageResult("5285", TradeDate, 110m, 100m, 115m, 90m, null, 45, CalculationStatus.InsufficientHistory, TradeDate, "僅累積 45 個有效交易日，MA120 尚缺 75 個有效交易日（逐檔檢查，非市場整體交易日數）。");

        var result = new StrategyEvaluationService().Evaluate(
            TradeDate, [CreateHolding(entryAveragePrice: 120m, currentPrice: 98m)], [ma], MarketTypesFor(MarketType.Listed), TaipeiNow);

        var alert = Assert.Single(result);
        Assert.Equal(AlertKind.MovingAverageTriggered, alert.AlertKind);
        Assert.True(alert.TriggeredMa5);
        Assert.True(alert.TriggeredMa20);
        Assert.False(alert.TriggeredMa120);
        Assert.Equal("歷史資料不足", alert.DiagnosticStatus);
        Assert.Equal(45, alert.AvailableTradingDayCount);
    }

    private static CustomerHolding CreateHolding(
        decimal? entryAveragePrice,
        decimal? currentPrice,
        string? entryAveragePriceIssue = null,
        string? currentPriceIssue = null,
        string code = "5285",
        string name = "宜鼎") => new(
        TradeDate,
        @"C:\Data\親帶績效.xlsx",
        "王保仁-A",
        "王保仁",
        4,
        code,
        name,
        currentPrice,
        8,
        $@"C:\DATA\親帶績效.XLSX|王保仁-A|4|{code}",
        currentPriceIssue,
        entryAveragePrice,
        entryAveragePriceIssue);

    private static MovingAverageResult CreateMovingAverage(decimal close, decimal ma5, decimal ma20, decimal ma60, decimal ma120) => new(
        "5285", TradeDate, close, ma5, ma20, ma60, ma120, 120, CalculationStatus.Ok);

    private static Dictionary<string, MarketType> MarketTypesFor(MarketType marketType)
        => new(StringComparer.OrdinalIgnoreCase) { ["5285"] = marketType };
}
