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

    private static CustomerHolding CreateHolding(decimal? currentPrice, string? currentPriceIssue = null) => new(
        TradeDate,
        @"C:\Data\親帶績效.xlsx",
        "王保仁-A",
        "王保仁",
        4,
        "5285",
        "宜鼎",
        currentPrice,
        8,
        @"C:\DATA\親帶績效.XLSX|王保仁-A|4|5285",
        currentPriceIssue);

    private static MovingAverageResult CreateMovingAverage(decimal close, decimal ma5, decimal ma20, decimal ma60, decimal ma120) => new(
        "5285", TradeDate, close, ma5, ma20, ma60, ma120, 120, CalculationStatus.Ok);

    private static Dictionary<string, MarketType> MarketTypesFor(MarketType marketType)
        => new(StringComparer.OrdinalIgnoreCase) { ["5285"] = marketType };
}
