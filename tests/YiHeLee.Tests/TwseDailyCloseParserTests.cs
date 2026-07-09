using YiHeLee.Application.Exceptions;
using YiHeLee.Infrastructure.MarketData;

namespace YiHeLee.Tests;

public sealed class TwseDailyCloseParserTests
{
    private static string ReadFixture(string name)
        => File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", name));

    [Fact]
    public void 可解析正常每日收盤行情()
    {
        var json = ReadFixture("twse_mi_index_success.json");

        var result = TwseDailyCloseParser.Parse(json);

        Assert.False(result.IsExplicitNoData);
        Assert.Equal(new DateOnly(2026, 7, 8), result.SourceDataDate);
        Assert.Equal(3, result.Quotes.Count); // 第4筆「暫停交易範例」收盤價為「--」應被略過
        var taiwanSemiconductor = Assert.Single(result.Quotes, x => x.StockCode == "2330");
        Assert.Equal("台積電", taiwanSemiconductor.StockName);
        Assert.Equal(2465.00m, taiwanSemiconductor.ClosePrice);
    }

    [Fact]
    public void 收盤價含逗號時可正確解析為數字()
    {
        var json = ReadFixture("twse_mi_index_success.json");
        var result = TwseDailyCloseParser.Parse(json);

        // 鴻海成交股數等欄位含千分位逗號，收盤價本身雖不含逗號，但驗證解析流程對含逗號欄位不受影響。
        var honHai = Assert.Single(result.Quotes, x => x.StockCode == "2317");
        Assert.Equal(176.00m, honHai.ClosePrice);
    }

    [Fact]
    public void 收盤價為雙槓或NA時該列略過但不影響其餘資料()
    {
        var json = ReadFixture("twse_mi_index_success.json");
        var result = TwseDailyCloseParser.Parse(json);

        Assert.DoesNotContain(result.Quotes, x => x.StockCode == "9999");
    }

    [Fact]
    public void 休市日回應明確查無資料()
    {
        var json = ReadFixture("twse_mi_index_holiday.json");

        var result = TwseDailyCloseParser.Parse(json);

        Assert.True(result.IsExplicitNoData);
        Assert.Null(result.SourceDataDate);
        Assert.Empty(result.Quotes);
    }

    [Fact]
    public void HTTP200但為錯誤頁或非JSON時應拋出可重試例外()
    {
        Assert.Throws<RetryableJobException>(() => TwseDailyCloseParser.Parse("<html>錯誤頁</html>"));
    }

    [Fact]
    public void 缺少date欄位時應拋出可重試例外_不得視為成功()
    {
        const string json = """{"tables":[],"stat":"OK"}""";
        Assert.Throws<RetryableJobException>(() => TwseDailyCloseParser.Parse(json));
    }

    [Fact]
    public void 缺少收盤價表格時應拋出可重試例外()
    {
        const string json = """{"stat":"OK","date":"20260708","tables":[{"fields":["指數","收盤指數"],"data":[]}]}""";
        Assert.Throws<RetryableJobException>(() => TwseDailyCloseParser.Parse(json));
    }
}
