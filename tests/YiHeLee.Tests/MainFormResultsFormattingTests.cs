using YiHeLee.App.Forms;
using YiHeLee.Domain;

namespace YiHeLee.Tests;

/// <summary>
/// 「符合通知條件」頁籤判斷明細文字測試。2026-07-19 正式策略以兩個子條件顯示：
/// TriggeredMa20 代表「進場價/平均價 &gt; MA20」、TriggeredMa5 代表「現價 &lt; MA5」；
/// 出現在該頁籤的持股一定兩項同時成立，判斷明細逐行列出兩個子條件與實際數值。MA120 不參與策略、不列入。
/// internal 方法由 <c>YiHeLee.App.csproj</c> 的 InternalsVisibleTo 開放給測試專案呼叫。
/// </summary>
public sealed class MainFormResultsFormattingTests
{
    private static readonly DateOnly TradeDate = new(2026, 7, 9);
    private static readonly DateTimeOffset CalculatedAt = new(2026, 7, 9, 13, 35, 0, TimeSpan.FromHours(8));

    [Fact]
    public void 判斷明細逐行列出進場價與現價兩個成立子條件()
    {
        // 進場價 120 > MA20 115、現價 98 < MA5 100 → 兩項同時成立。
        var alert = CreateAlert(
            entryAveragePrice: 120m, currentPrice: 98m,
            ma5: 100m, ma20: 115m, ma120: 90m,
            triggeredMa5: true, triggeredMa20: true, triggeredMa120: false);

        var detail = MainForm.BuildJudgmentDetail(alert);
        var lines = detail.Split("\r\n");

        Assert.Equal(2, lines.Length);
        Assert.Equal("進場價/平均價 120 > MA20 115", lines[0]);
        Assert.Equal("現價 98 < MA5 100", lines[1]);
        Assert.DoesNotContain("MA120", detail, StringComparison.Ordinal);
    }

    [Fact]
    public void 判斷明細只顯示實際成立的子條件()
    {
        // 防禦性：只有進場價 > MA20 成立時，只顯示該行（現價子條件不顯示）。
        var alert = CreateAlert(
            entryAveragePrice: 120m, currentPrice: 130m,
            ma5: 100m, ma20: 115m, ma120: 90m,
            triggeredMa5: false, triggeredMa20: true, triggeredMa120: false);

        var detail = MainForm.BuildJudgmentDetail(alert);

        Assert.Equal("進場價/平均價 120 > MA20 115", detail);
        Assert.DoesNotContain("現價", detail, StringComparison.Ordinal);
    }

    [Fact]
    public void 兩個子條件都未成立時_判斷明細為空()
    {
        var alert = CreateAlert(
            entryAveragePrice: 100m, currentPrice: 200m,
            ma5: 100m, ma20: 115m, ma120: 90m,
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
        110m,
        ma5,
        ma20,
        90m,
        ma120,
        triggeredMa5,
        triggeredMa20,
        triggeredMa120,
        "符合通知條件：進場價/平均價 120 > MA20 115；現價 98 < MA5 100。",
        MarketType.Otc,
        null,
        null,
        "TPEx",
        CalculatedAt,
        EntryAveragePrice: entryAveragePrice,
        EntryAveragePriceIssue: null,
        CurrentPriceIssue: null);
}
