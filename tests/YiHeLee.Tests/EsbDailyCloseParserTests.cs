using YiHeLee.Application.Exceptions;
using YiHeLee.Infrastructure.MarketData;

namespace YiHeLee.Tests;

public sealed class EsbDailyCloseParserTests
{
    private static string ReadFixture(string name)
        => File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", name));

    [Fact]
    public void 可解析正常興櫃當日行情()
    {
        var json = ReadFixture("esb_daily_statistics_success.json");

        var result = EsbDailyCloseParser.Parse(json);

        Assert.False(result.IsExplicitNoData);
        Assert.Equal(new DateOnly(2026, 7, 9), result.SourceDataDate);
        Assert.Equal(2, result.Quotes.Count); // 1234 全日無成交（LatestPrice 空白）應被略過
        var item = Assert.Single(result.Quotes, x => x.StockCode == "4573");
        Assert.Equal("高明鐵", item.StockName);
        Assert.Equal(408.00m, item.ClosePrice);
    }

    [Fact]
    public void 最後成交價含千分位逗號可正確解析()
    {
        var json = ReadFixture("esb_daily_statistics_success.json");
        var result = EsbDailyCloseParser.Parse(json);

        var item = Assert.Single(result.Quotes, x => x.StockCode == "6990");
        Assert.Equal(1220.00m, item.ClosePrice);
    }

    [Fact]
    public void 回應資料日期與目標日期不符時_Service層必須能偵測到日期不一致()
    {
        var json = ReadFixture("esb_daily_statistics_stale_date.json");
        var requestedDate = new DateOnly(2026, 7, 9);

        var result = EsbDailyCloseParser.Parse(json);

        Assert.NotNull(result.SourceDataDate);
        Assert.NotEqual(requestedDate, result.SourceDataDate);
        Assert.Equal(new DateOnly(2026, 7, 8), result.SourceDataDate);
    }

    [Fact]
    public void HTTP200但為錯誤頁或非JSON時應拋出可重試例外()
    {
        Assert.Throws<RetryableJobException>(() => EsbDailyCloseParser.Parse("<html>錯誤頁</html>"));
    }

    [Fact]
    public void 回應不是陣列格式時應拋出可重試例外()
    {
        Assert.Throws<RetryableJobException>(() => EsbDailyCloseParser.Parse("""{"error":"unexpected"}"""));
    }

    [Fact]
    public void 空陣列視為明確查無資料()
    {
        var result = EsbDailyCloseParser.Parse("[]");
        Assert.True(result.IsExplicitNoData);
        Assert.Empty(result.Quotes);
    }
}
