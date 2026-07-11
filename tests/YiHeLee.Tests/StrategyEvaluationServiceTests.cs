using YiHeLee.Application.Services;
using YiHeLee.Domain;

namespace YiHeLee.Tests;

public sealed class StrategyEvaluationServiceTests
{
    private static readonly DateOnly TradeDate = new(2026, 7, 9);
    private static readonly DateTimeOffset TaipeiNow = new(2026, 7, 9, 13, 35, 0, TimeSpan.FromHours(8));

    [Fact]
    public void 任一均價小於等於現價_就產生通知()
    {
        var holding = CreateHolding(currentPrice: 100m);
        var ma = CreateMovingAverage(close: 105m, ma5: 110m, ma20: 100m, ma60: 108m, ma120: 120m);

        var result = new StrategyEvaluationService().Evaluate(
            TradeDate, [holding], [ma], MarketTypesFor(MarketType.Listed), TaipeiNow);

        var alert = Assert.Single(result);
        Assert.Equal(AlertKind.MovingAverageTriggered, alert.AlertKind);
        Assert.False(alert.TriggeredMa5);
        Assert.True(alert.TriggeredMa20);
        Assert.False(alert.TriggeredMa120);
        Assert.Equal("TWSE", alert.PriceSourceProvider);
        Assert.Equal(TaipeiNow, alert.CalculatedAt);
    }

    [Fact]
    public void 三項均價都高於現價_不產生通知()
    {
        var result = new StrategyEvaluationService().Evaluate(
            TradeDate,
            [CreateHolding(currentPrice: 100m)],
            [CreateMovingAverage(close: 105m, ma5: 101m, ma20: 102m, ma60: 103m, ma120: 103m)],
            MarketTypesFor(MarketType.Listed),
            TaipeiNow);

        Assert.Empty(result);
    }

    [Fact]
    public void 現價無效時_產生現價異常通知且不參與均線判斷()
    {
        // 即使均線資料齊全且遠低於任何價格，現價無效一律不得判斷是否觸發，必須告知使用者。
        var holding = CreateHolding(currentPrice: null, currentPriceIssue: "儲存格為 #N/A（DDE 尚未取得資料，看盤軟體可能未開啟或未連線）");
        var ma = CreateMovingAverage(close: 10m, ma5: 1m, ma20: 1m, ma60: 1m, ma120: 1m);

        var result = new StrategyEvaluationService().Evaluate(
            TradeDate, [holding], [ma], MarketTypesFor(MarketType.Listed), TaipeiNow);

        var alert = Assert.Single(result);
        Assert.Equal(AlertKind.CurrentPriceInvalid, alert.AlertKind);
        Assert.Null(alert.CurrentPrice);
        Assert.False(alert.TriggeredMa5);
        Assert.False(alert.TriggeredMa20);
        Assert.False(alert.TriggeredMa120);
        Assert.Contains("#N/A", alert.TriggerDescription, StringComparison.Ordinal);
        Assert.Contains("DDE", alert.TriggerDescription, StringComparison.Ordinal);

        // MA5／MA20／MA60／MA120 是依官方收盤價自行計算，與現價（DDE）無關；即使現價異常，
        // 「每日五日均價策略」頁籤仍必須顯示已算出的均價，不得因現價異常而留白。
        Assert.Equal(10m, alert.ClosePrice);
        Assert.Equal(1m, alert.MovingAverage5);
        Assert.Equal(1m, alert.MovingAverage20);
        Assert.Equal(1m, alert.MovingAverage60);
        Assert.Equal(1m, alert.MovingAverage120);
    }

    [Fact]
    public void 現價無效且官方收盤價尚未算出時_均價欄位維持null()
    {
        var holding = CreateHolding(currentPrice: null, currentPriceIssue: "儲存格為空白");

        var result = new StrategyEvaluationService().Evaluate(
            TradeDate, [holding], [], MarketTypesFor(MarketType.Listed), TaipeiNow);

        var alert = Assert.Single(result);
        Assert.Equal(AlertKind.CurrentPriceInvalid, alert.AlertKind);
        Assert.Null(alert.ClosePrice);
        Assert.Null(alert.MovingAverage5);
        Assert.Null(alert.MovingAverage20);
        Assert.Null(alert.MovingAverage60);
        Assert.Null(alert.MovingAverage120);
    }

    [Fact]
    public void 找不到官方收盤價_列入無法判斷清單()
    {
        var result = new StrategyEvaluationService().Evaluate(
            TradeDate,
            [CreateHolding(currentPrice: 100m)],
            [],
            MarketTypesFor(MarketType.Listed),
            TaipeiNow);

        var alert = Assert.Single(result);
        Assert.Equal(AlertKind.TechnicalIndicatorMissing, alert.AlertKind);
    }

    [Fact]
    public void MA120交易日數不足時_不得觸發也不得硬算()
    {
        // MA5、MA20 資料足夠，MA120 因交易日數不足而為 null；現價設得極高，只有 MA120 若被硬算才會觸發。
        var ma = new MovingAverageResult("5285", TradeDate, 90m, 85m, 88m, 92m, null, 45, CalculationStatus.InsufficientHistory);

        var result = new StrategyEvaluationService().Evaluate(
            TradeDate,
            [CreateHolding(currentPrice: 1000m)],
            [ma],
            MarketTypesFor(MarketType.Listed),
            TaipeiNow);

        var alert = Assert.Single(result);
        Assert.Equal(AlertKind.MovingAverageTriggered, alert.AlertKind);
        Assert.True(alert.TriggeredMa5);
        Assert.True(alert.TriggeredMa20);
        Assert.False(alert.TriggeredMa120);
        Assert.Null(alert.MovingAverage120);
    }

    [Fact]
    public void 上櫃股票資料來源標示為TPEx()
    {
        var ma = CreateMovingAverage(close: 50m, ma5: 60m, ma20: 61m, ma60: 62m, ma120: 63m);

        var result = new StrategyEvaluationService().Evaluate(
            TradeDate,
            [CreateHolding(currentPrice: 100m)],
            [ma],
            MarketTypesFor(MarketType.Otc),
            TaipeiNow);

        Assert.Equal("TPEx", Assert.Single(result).PriceSourceProvider);
    }

    [Fact]
    public void 股票代碼無法識別時_列入無法判斷清單並標示原因()
    {
        // 8 位數金額（例如權益數）不得被當成股票代碼判斷；resolutions 由 StockIdentityResolutionService 產生。
        var holding = CreateHolding(currentPrice: 100m, code: "10037677", name: "金額誤判");
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
        var holding = CreateHolding(currentPrice: 5m, code: "070001", name: "測試權證");
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
        var holding = CreateHolding(currentPrice: 100m);
        var ma = CreateMovingAverage(close: 105m, ma5: 110m, ma20: 100m, ma60: 108m, ma120: 120m);

        var result = new StrategyEvaluationService().Evaluate(
            TradeDate, [holding], [ma], MarketTypesFor(MarketType.Listed), TaipeiNow);

        var alert = Assert.Single(result);
        Assert.Equal("正常", alert.DiagnosticStatus);
        Assert.Equal(120, alert.AvailableTradingDayCount);
    }

    [Fact]
    public void 逐檔歷史資料不足但部分均線已觸發時_仍產生通知並標示歷史資料不足()
    {
        // 只有 MA5（5個有效交易日），MA20/60/120 因逐檔資料不足為 null；現價使 MA5 觸發。
        var ma = new MovingAverageResult("5285", TradeDate, 90m, 85m, null, null, null, 5, CalculationStatus.InsufficientHistory, TradeDate, "僅累積 5 個有效交易日，MA120 尚缺 115 個有效交易日（逐檔檢查，非市場整體交易日數）。");

        var result = new StrategyEvaluationService().Evaluate(
            TradeDate, [CreateHolding(currentPrice: 100m)], [ma], MarketTypesFor(MarketType.Listed), TaipeiNow);

        var alert = Assert.Single(result);
        Assert.Equal(AlertKind.MovingAverageTriggered, alert.AlertKind);
        Assert.True(alert.TriggeredMa5);
        Assert.Equal("歷史資料不足", alert.DiagnosticStatus);
        Assert.Equal(5, alert.AvailableTradingDayCount);
    }

    private static CustomerHolding CreateHolding(decimal? currentPrice, string? currentPriceIssue = null, string code = "5285", string name = "宜鼎") => new(
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
        currentPriceIssue);

    private static MovingAverageResult CreateMovingAverage(decimal close, decimal ma5, decimal ma20, decimal ma60, decimal ma120) => new(
        "5285", TradeDate, close, ma5, ma20, ma60, ma120, 120, CalculationStatus.Ok);

    private static Dictionary<string, MarketType> MarketTypesFor(MarketType marketType)
        => new(StringComparer.OrdinalIgnoreCase) { ["5285"] = marketType };
}
