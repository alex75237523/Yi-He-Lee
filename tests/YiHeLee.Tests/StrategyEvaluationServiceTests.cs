using YiHeLee.Application.Services;
using YiHeLee.Domain;

namespace YiHeLee.Tests;

public sealed class StrategyEvaluationServiceTests
{
    private static readonly DateOnly TradeDate = new(2026, 7, 9);
    private static readonly DateTimeOffset TaipeiNow = new(2026, 7, 9, 13, 35, 0, TimeSpan.FromHours(8));

    [Fact]
    public void 任一均價小於等於進場價_就產生通知()
    {
        var holding = CreateHolding(entryPrice: 100m);
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
    public void 三項均價都高於進場價_不產生通知()
    {
        var result = new StrategyEvaluationService().Evaluate(
            TradeDate,
            [CreateHolding(entryPrice: 100m)],
            [CreateMovingAverage(close: 105m, ma5: 101m, ma20: 102m, ma60: 103m, ma120: 103m)],
            MarketTypesFor(MarketType.Listed),
            TaipeiNow);

        Assert.Empty(result);
    }

    [Fact]
    public void 找不到官方收盤價_列入無法判斷清單()
    {
        var result = new StrategyEvaluationService().Evaluate(
            TradeDate,
            [CreateHolding(entryPrice: 100m)],
            [],
            MarketTypesFor(MarketType.Listed),
            TaipeiNow);

        var alert = Assert.Single(result);
        Assert.Equal(AlertKind.TechnicalIndicatorMissing, alert.AlertKind);
    }

    [Fact]
    public void MA120交易日數不足時_不得觸發也不得硬算()
    {
        // MA5、MA20 資料足夠，MA120 因交易日數不足而為 null；進場價設得極高，只有 MA120 若被硬算才會觸發。
        var ma = new MovingAverageResult("5285", TradeDate, 90m, 85m, 88m, 92m, null, 45, CalculationStatus.InsufficientHistory);

        var result = new StrategyEvaluationService().Evaluate(
            TradeDate,
            [CreateHolding(entryPrice: 1000m)],
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
            [CreateHolding(entryPrice: 100m)],
            [ma],
            MarketTypesFor(MarketType.Otc),
            TaipeiNow);

        Assert.Equal("TPEx", Assert.Single(result).PriceSourceProvider);
    }

    private static CustomerHolding CreateHolding(decimal entryPrice) => new(
        TradeDate,
        @"C:\Data\親帶績效.xlsx",
        "王保仁-A",
        "王保仁",
        4,
        "5285",
        "宜鼎",
        entryPrice,
        8,
        @"C:\DATA\親帶績效.XLSX|王保仁-A|4|5285");

    private static MovingAverageResult CreateMovingAverage(decimal close, decimal ma5, decimal ma20, decimal ma60, decimal ma120) => new(
        "5285", TradeDate, close, ma5, ma20, ma60, ma120, 120, CalculationStatus.Ok);

    private static Dictionary<string, MarketType> MarketTypesFor(MarketType marketType)
        => new(StringComparer.OrdinalIgnoreCase) { ["5285"] = marketType };
}
