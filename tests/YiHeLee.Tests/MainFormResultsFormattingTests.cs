using YiHeLee.App.Forms;
using YiHeLee.Domain;

namespace YiHeLee.Tests;

/// <summary>
/// 「符合均價條件」頁籤判斷明細文字測試。輸出文字必須讓一般使用者不需理解程式邏輯即可看懂為何成立，
/// 同時包含「進場價/平均價」「現價」「對應均價」「兩者皆達標」等字樣。
/// internal 方法由 <c>YiHeLee.App.csproj</c> 的 InternalsVisibleTo 開放給測試專案呼叫。
/// </summary>
public sealed class MainFormResultsFormattingTests
{
    private static readonly DateOnly TradeDate = new(2026, 7, 9);
    private static readonly DateTimeOffset CalculatedAt = new(2026, 7, 9, 13, 35, 0, TimeSpan.FromHours(8));

    [Fact]
    public void 判斷明細同時包含進場價平均價現價對應均價與兩者皆達標()
    {
        var alert = CreateAlert(
            entryAveragePrice: 501m, currentPrice: 520m,
            ma5: 600m, ma20: 480m, ma120: 600m,
            triggeredMa5: false, triggeredMa20: true, triggeredMa120: false);

        var detail = MainForm.BuildJudgmentDetail(alert);

        Assert.Contains("進場價/平均價", detail, StringComparison.Ordinal);
        Assert.Contains("現價", detail, StringComparison.Ordinal);
        Assert.Contains("MA20", detail, StringComparison.Ordinal);
        Assert.Contains("兩者皆達標", detail, StringComparison.Ordinal);
        Assert.Contains("501", detail, StringComparison.Ordinal);
        Assert.Contains("520", detail, StringComparison.Ordinal);
        Assert.Contains("480", detail, StringComparison.Ordinal);
    }

    [Fact]
    public void 同時符合多條均價時_判斷明細以換行分隔且各自列出對應均價()
    {
        var alert = CreateAlert(
            entryAveragePrice: 501m, currentPrice: 520m,
            ma5: 490m, ma20: 480m, ma120: 600m,
            triggeredMa5: true, triggeredMa20: true, triggeredMa120: false);

        var detail = MainForm.BuildJudgmentDetail(alert);
        var lines = detail.Split("\r\n");

        Assert.Equal(2, lines.Length);
        Assert.Contains("MA5", lines[0], StringComparison.Ordinal);
        Assert.Contains("MA20", lines[1], StringComparison.Ordinal);
        Assert.All(lines, line => Assert.Contains("兩者皆達標", line, StringComparison.Ordinal));
    }

    [Fact]
    public void 未觸發任何均價時_判斷明細為空()
    {
        var alert = CreateAlert(
            entryAveragePrice: 100m, currentPrice: 100m,
            ma5: 600m, ma20: 600m, ma120: 600m,
            triggeredMa5: false, triggeredMa20: false, triggeredMa120: false);

        Assert.Equal(string.Empty, MainForm.BuildJudgmentDetail(alert));
    }

    [Fact]
    public void FormatEntryAveragePrice無效時顯示無效且不提及DDE()
    {
        var text = MainForm.FormatEntryAveragePrice(null);

        Assert.Equal("無效", text);
        Assert.DoesNotContain("DDE", text, StringComparison.Ordinal);
    }

    [Fact]
    public void FormatCurrentPrice無效時顯示DDE字樣()
    {
        var text = MainForm.FormatCurrentPrice(null);

        Assert.Contains("DDE", text, StringComparison.Ordinal);
    }

    private static StrategyAlert CreateAlert(
        decimal entryAveragePrice,
        decimal currentPrice,
        decimal ma5,
        decimal ma20,
        decimal ma120,
        bool triggeredMa5,
        bool triggeredMa20,
        bool triggeredMa120) => new(
        TradeDate,
        AlertKind.MovingAverageTriggered,
        @"C:\Data\親帶績效.xlsx",
        "王保仁-A",
        "王保仁",
        4,
        "5285",
        "宜鼎",
        currentPrice,
        8,
        480m,
        ma5,
        ma20,
        480m,
        ma120,
        triggeredMa5,
        triggeredMa20,
        triggeredMa120,
        "進場價/平均價與現價已同時大於或等於：測試",
        MarketType.Otc,
        null,
        null,
        "TPEx",
        CalculatedAt,
        EntryAveragePrice: entryAveragePrice,
        EntryAveragePriceIssue: null,
        CurrentPriceIssue: null);
}
