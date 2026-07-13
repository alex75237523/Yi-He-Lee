using YiHeLee.Application.Services;
using YiHeLee.Domain;

namespace YiHeLee.Tests;

/// <summary>
/// 2026-07-13 正式更正比較方向：客戶 Excel「進場價/平均價」與「現價」是兩個完全獨立、不得混用的欄位；
/// 每一條均價（MA5／MA20／MA120）只要大於或等於「進場價/平均價」或「現價」其中一個價格就算成立。
/// MA60 只保存與顯示，不參與觸發。任一價格無效都不得觸發，且必須各自產生對應的異常通知。
/// </summary>
public sealed class StrategyEvaluationServiceTests
{
    private static readonly DateOnly TradeDate = new(2026, 7, 9);
    private static readonly DateTimeOffset TaipeiNow = new(2026, 7, 9, 13, 35, 0, TimeSpan.FromHours(8));

    [Fact]
    public void MA高於兩個價格時_MA5觸發()
    {
        var holding = CreateHolding(entryAveragePrice: 480m, currentPrice: 490m);
        var ma = CreateMovingAverage(close: 490m, ma5: 500m, ma20: 470m, ma60: 600m, ma120: 470m);

        var result = new StrategyEvaluationService().Evaluate(
            TradeDate, [holding], [ma], MarketTypesFor(MarketType.Listed), TaipeiNow);

        var alert = Assert.Single(result);
        Assert.Equal(AlertKind.MovingAverageTriggered, alert.AlertKind);
        Assert.True(alert.TriggeredMa5);
        Assert.False(alert.TriggeredMa20);
        Assert.False(alert.TriggeredMa120);
    }

    [Fact]
    public void 進場價與現價都等於MA20時_MA20觸發_等於也算成立()
    {
        var holding = CreateHolding(entryAveragePrice: 480m, currentPrice: 480m);
        var ma = CreateMovingAverage(close: 480m, ma5: 470m, ma20: 480m, ma60: 480m, ma120: 470m);

        var result = new StrategyEvaluationService().Evaluate(
            TradeDate, [holding], [ma], MarketTypesFor(MarketType.Listed), TaipeiNow);

        var alert = Assert.Single(result);
        Assert.False(alert.TriggeredMa5);
        Assert.True(alert.TriggeredMa20);
        Assert.False(alert.TriggeredMa120);
    }

    [Fact]
    public void 只高於進場價時_仍觸發()
    {
        // MA20 480 >= 進場價 470 成立，即使 MA20 480 >= 現價 500 不成立，仍應觸發。
        var holding = CreateHolding(entryAveragePrice: 470m, currentPrice: 500m);
        var ma = CreateMovingAverage(close: 480m, ma5: 460m, ma20: 480m, ma60: 900m, ma120: 460m);

        var result = new StrategyEvaluationService().Evaluate(
            TradeDate, [holding], [ma], MarketTypesFor(MarketType.Listed), TaipeiNow);

        var alert = Assert.Single(result);
        Assert.True(alert.TriggeredMa20);
    }

    [Fact]
    public void 只高於現價時_仍觸發()
    {
        // MA20 480 >= 現價 470 成立，即使 MA20 480 >= 進場價 500 不成立，仍應觸發。
        var holding = CreateHolding(entryAveragePrice: 500m, currentPrice: 470m);
        var ma = CreateMovingAverage(close: 480m, ma5: 460m, ma20: 480m, ma60: 900m, ma120: 460m);

        var result = new StrategyEvaluationService().Evaluate(
            TradeDate, [holding], [ma], MarketTypesFor(MarketType.Listed), TaipeiNow);

        var alert = Assert.Single(result);
        Assert.True(alert.TriggeredMa20);
    }

    [Fact]
    public void 舊方向價格高於均價時_不得再觸發()
    {
        var holding = CreateHolding(entryAveragePrice: 501m, currentPrice: 520m);
        var ma = CreateMovingAverage(close: 480m, ma5: 480m, ma20: 470m, ma60: 900m, ma120: 470m);

        var result = new StrategyEvaluationService().Evaluate(
            TradeDate, [holding], [ma], MarketTypesFor(MarketType.Listed), TaipeiNow);

        Assert.Empty(result);
    }

    [Fact]
    public void MA5未通過但MA20兩者都通過時_整體仍觸發()
    {
        var holding = CreateHolding(entryAveragePrice: 470m, currentPrice: 460m);
        // MA5=450（低於任一價格），MA20=480（高於進場價與現價）。
        var ma = CreateMovingAverage(close: 480m, ma5: 450m, ma20: 480m, ma60: 900m, ma120: 450m);

        var result = new StrategyEvaluationService().Evaluate(
            TradeDate, [holding], [ma], MarketTypesFor(MarketType.Listed), TaipeiNow);

        var alert = Assert.Single(result);
        Assert.Equal(AlertKind.MovingAverageTriggered, alert.AlertKind);
        Assert.False(alert.TriggeredMa5);
        Assert.True(alert.TriggeredMa20);
    }

    [Fact]
    public void MA120交易日數不足為null時_不得觸發也不得硬算()
    {
        var holding = CreateHolding(entryAveragePrice: 80m, currentPrice: 82m);
        var ma = new MovingAverageResult("5285", TradeDate, 90m, 85m, 88m, 92m, null, 45, CalculationStatus.InsufficientHistory);

        var result = new StrategyEvaluationService().Evaluate(
            TradeDate, [holding], [ma], MarketTypesFor(MarketType.Listed), TaipeiNow);

        var alert = Assert.Single(result);
        Assert.True(alert.TriggeredMa5);
        Assert.True(alert.TriggeredMa20);
        Assert.False(alert.TriggeredMa120);
        Assert.Null(alert.MovingAverage120);
    }

    [Fact]
    public void MA60即使兩者通過也不參與觸發()
    {
        // MA60 高於兩個價格，但 MA5／MA20／MA120 皆低於任一價格：不得因 MA60 而觸發。
        var holding = CreateHolding(entryAveragePrice: 500m, currentPrice: 520m);
        var ma = CreateMovingAverage(close: 900m, ma5: 1m, ma20: 1m, ma60: 1000m, ma120: 1m);

        var result = new StrategyEvaluationService().Evaluate(
            TradeDate, [holding], [ma], MarketTypesFor(MarketType.Listed), TaipeiNow);

        Assert.Empty(result);
    }

    [Fact]
    public void 進場價空白時_產生進場價平均價異常且不觸發()
    {
        var holding = CreateHolding(entryAveragePrice: null, currentPrice: 1000m, entryAveragePriceIssue: "儲存格為空白，無法讀取進場價/平均價");
        var ma = CreateMovingAverage(close: 10m, ma5: 1000m, ma20: 1000m, ma60: 1000m, ma120: 1000m);

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
    public void 進場價為負數時_產生進場價平均價異常()
    {
        var holding = CreateHolding(entryAveragePrice: null, currentPrice: 100m, entryAveragePriceIssue: "數值為 -5（非正數），非有效的進場價/平均價");

        var result = new StrategyEvaluationService().Evaluate(
            TradeDate, [holding], [], MarketTypesFor(MarketType.Listed), TaipeiNow);

        var alert = Assert.Single(result);
        Assert.Equal(AlertKind.EntryAveragePriceInvalid, alert.AlertKind);
        Assert.Contains("非正數", alert.MissingReason, StringComparison.Ordinal);
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
        var holding = CreateHolding(entryAveragePrice: 100m, currentPrice: null, currentPriceIssue: "儲存格為 #N/A（DDE 尚未取得資料，看盤軟體可能未開啟或未連線）");
        var ma = CreateMovingAverage(close: 10m, ma5: 1000m, ma20: 1000m, ma60: 1000m, ma120: 1000m);

        var result = new StrategyEvaluationService().Evaluate(
            TradeDate, [holding], [ma], MarketTypesFor(MarketType.Listed), TaipeiNow);

        var alert = Assert.Single(result);
        Assert.Equal(AlertKind.CurrentPriceInvalid, alert.AlertKind);
        Assert.Null(alert.CurrentPrice);
        Assert.Equal(100m, alert.EntryAveragePrice);
        Assert.Contains("#N/A", alert.TriggerDescription, StringComparison.Ordinal);
        Assert.Contains("DDE", alert.TriggerDescription, StringComparison.Ordinal);

        // MA5／MA20／MA60／MA120 是依官方收盤價自行計算，與現價（DDE）無關；即使現價異常，
        // 「每日五日均價策略」頁籤仍必須顯示已算出的均價，不得因現價異常而留白。
        Assert.Equal(10m, alert.ClosePrice);
        Assert.Equal(1000m, alert.MovingAverage5);
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
        // 兩組欄位的原因不得共用或互相代替。
        Assert.NotEqual(entryAlert.TriggerDescription, currentAlert.TriggerDescription);
    }

    [Fact]
    public void 不得以收盤價代替進場價或現價()
    {
        // 收盤價與 MA20 相等，但進場價與現價都低於 MA20：不得誤用收盤價判斷觸發。
        var holding = CreateHolding(entryAveragePrice: 500m, currentPrice: 500m);
        var ma = CreateMovingAverage(close: 480m, ma5: 470m, ma20: 480m, ma60: 480m, ma120: 470m);

        var result = new StrategyEvaluationService().Evaluate(
            TradeDate, [holding], [ma], MarketTypesFor(MarketType.Listed), TaipeiNow);

        Assert.Empty(result);
    }

    [Fact]
    public void EvaluateAll與Evaluate的均線判斷完全一致()
    {
        var holdings = new[]
        {
            CreateHolding(entryAveragePrice: 480m, currentPrice: 490m, code: "5285", name: "宜鼎"),
            CreateHolding(entryAveragePrice: 470m, currentPrice: 500m, code: "2330", name: "台積電"),
            CreateHolding(entryAveragePrice: null, currentPrice: 520m, code: "3691", name: "碩禾", entryAveragePriceIssue: "儲存格為空白，無法讀取進場價/平均價"),
        };
        var mas = new[]
        {
            CreateMovingAverage(close: 480m, ma5: 500m, ma20: 470m, ma60: 480m, ma120: 470m) with { StockCode = "5285" },
            CreateMovingAverage(close: 480m, ma5: 460m, ma20: 460m, ma60: 480m, ma120: 460m) with { StockCode = "2330" },
            CreateMovingAverage(close: 480m, ma5: 500m, ma20: 480m, ma60: 480m, ma120: 470m) with { StockCode = "3691" },
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
        Assert.False(notTriggeredResult.Ma5Match);
        Assert.False(notTriggeredResult.Ma20Match);

        var invalidEntryAlert = Assert.Single(alerts, x => x.StockCode == "3691");
        Assert.Equal(AlertKind.EntryAveragePriceInvalid, invalidEntryAlert.AlertKind);
        var invalidEntryResult = Assert.Single(allResults, x => x.RawStockCode == "3691");
        Assert.Equal("無效", invalidEntryResult.EntryAveragePriceStatus);
        Assert.Equal("進場價/平均價無效，暫時無法判斷", invalidEntryResult.OverallResult);
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
        var ma = CreateMovingAverage(close: 50m, ma5: 120m, ma20: 121m, ma60: 62m, ma120: 123m);

        var result = new StrategyEvaluationService().Evaluate(
            TradeDate,
            [CreateHolding(entryAveragePrice: 100m, currentPrice: 100m)],
            [ma],
            MarketTypesFor(MarketType.Otc),
            TaipeiNow);

        Assert.Equal("TPEx", Assert.Single(result).PriceSourceProvider);
    }

    [Fact]
    public void 股票代碼無法識別時_列入無法判斷清單並標示原因()
    {
        // 8 位數金額（例如權益數）不得被當成股票代碼判斷；resolutions 由 StockIdentityResolutionService 產生。
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
        var holding = CreateHolding(entryAveragePrice: 480m, currentPrice: 490m);
        var ma = CreateMovingAverage(close: 480m, ma5: 500m, ma20: 470m, ma60: 480m, ma120: 470m);

        var result = new StrategyEvaluationService().Evaluate(
            TradeDate, [holding], [ma], MarketTypesFor(MarketType.Listed), TaipeiNow);

        var alert = Assert.Single(result);
        Assert.Equal("正常", alert.DiagnosticStatus);
        Assert.Equal(120, alert.AvailableTradingDayCount);
    }

    [Fact]
    public void 逐檔歷史資料不足但部分均線已觸發時_仍產生通知並標示歷史資料不足()
    {
        // 只有 MA5（5個有效交易日），MA20/60/120 因逐檔資料不足為 null；MA5 高於進場價與現價而觸發。
        var ma = new MovingAverageResult("5285", TradeDate, 90m, 85m, null, null, null, 5, CalculationStatus.InsufficientHistory, TradeDate, "僅累積 5 個有效交易日，MA120 尚缺 115 個有效交易日（逐檔檢查，非市場整體交易日數）。");

        var result = new StrategyEvaluationService().Evaluate(
            TradeDate, [CreateHolding(entryAveragePrice: 80m, currentPrice: 82m)], [ma], MarketTypesFor(MarketType.Listed), TaipeiNow);

        var alert = Assert.Single(result);
        Assert.Equal(AlertKind.MovingAverageTriggered, alert.AlertKind);
        Assert.True(alert.TriggeredMa5);
        Assert.Equal("歷史資料不足", alert.DiagnosticStatus);
        Assert.Equal(5, alert.AvailableTradingDayCount);
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
