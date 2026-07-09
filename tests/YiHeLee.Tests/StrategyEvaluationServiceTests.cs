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
        var indicator = CreateIndicator(ma5: 110m, ma20: 100m, ma120: 120m);

        var result = new StrategyEvaluationService().Evaluate(TradeDate, [holding], [indicator]);

        var alert = Assert.Single(result);
        Assert.Equal(AlertKind.MovingAverageTriggered, alert.AlertKind);
        Assert.False(alert.TriggeredMa5);
        Assert.True(alert.TriggeredMa20);
        Assert.False(alert.TriggeredMa120);
    }

    [Fact]
    public void 三項均價都高於進場價_不產生通知()
    {
        var result = new StrategyEvaluationService().Evaluate(
            TradeDate,
            [CreateHolding(entryPrice: 100m)],
            [CreateIndicator(ma5: 101m, ma20: 102m, ma120: 103m)]);

        Assert.Empty(result);
    }

    [Fact]
    public void 找不到股票技術資料_列入無法判斷清單()
    {
        var result = new StrategyEvaluationService().Evaluate(
            TradeDate,
            [CreateHolding(entryPrice: 100m)],
            []);

        var alert = Assert.Single(result);
        Assert.Equal(AlertKind.TechnicalIndicatorMissing, alert.AlertKind);
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

    private static TechnicalIndicator CreateIndicator(decimal ma5, decimal ma20, decimal ma120) => new(
        TradeDate,
        IndicatorType.BullishAlignment,
        MarketType.Listed,
        "5285",
        "宜鼎",
        1515m,
        ma5,
        ma20,
        115m,
        ma120,
        "https://www.cnyes.com/twstock/a_technical4.aspx",
        TaipeiNow,
        TaipeiNow);
}
