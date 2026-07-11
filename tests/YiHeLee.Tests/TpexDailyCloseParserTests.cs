using YiHeLee.Application.Exceptions;
using YiHeLee.Infrastructure.MarketData;

namespace YiHeLee.Tests;

public sealed class TpexDailyCloseParserTests
{
    private static string ReadFixture(string name)
        => File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", name));

    [Fact]
    public void 可解析正常上櫃收盤行情()
    {
        var json = ReadFixture("tpex_daily_close_success.json");

        var result = TpexDailyCloseParser.Parse(json);

        Assert.False(result.IsExplicitNoData);
        Assert.Equal(new DateOnly(2026, 7, 9), result.SourceDataDate);
        Assert.Equal(2, result.Quotes.Count); // 6488 收盤為「--」應被略過
        var item = Assert.Single(result.Quotes, x => x.StockCode == "5285");
        Assert.Equal("宜鼎", item.StockName);
        Assert.Equal(515.00m, item.ClosePrice);
    }

    [Fact]
    public void 收盤價含空白與千分位逗號可正確解析()
    {
        var json = ReadFixture("tpex_daily_close_success.json");
        var result = TpexDailyCloseParser.Parse(json);

        var etf = Assert.Single(result.Quotes, x => x.StockCode == "006201");
        Assert.Equal(46.34m, etf.ClosePrice);
    }

    [Fact]
    public void 可解析新版TPEx每日收盤行情端點格式()
    {
        const string json = """
            {
              "tables": [
                {
                  "title": "上櫃股票每日收盤行情(不含定價)",
                  "date": "115/07/08",
                  "category": "所有證券(不含權證、牛熊證)",
                  "totalCount": 2,
                  "fields": ["代號","名稱","收盤 ","漲跌","開盤 ","最高 ","最低"],
                  "data": [
                    ["5351","鈺創","98.20","+0.20","98.00","99.00","97.50"],
                    ["6488","環球晶","--","0.00","--","--","--"]
                  ]
                }
              ]
            }
            """;

        var result = TpexDailyCloseParser.Parse(json);

        Assert.Equal(new DateOnly(2026, 7, 8), result.SourceDataDate);
        var item = Assert.Single(result.Quotes);
        Assert.Equal("5351", item.StockCode);
        Assert.Equal("鈺創", item.StockName);
        Assert.Equal(98.20m, item.ClosePrice);
    }

    [Fact]
    public void 回應日期與目標日期不符時_Service層必須能偵測到日期不一致()
    {
        // 這正是實際觀測到的官方行為：查詢當日資料，但來源靜默改回傳前一交易日資料且 HTTP 200。
        var json = ReadFixture("tpex_daily_close_stale_date.json");
        var requestedDate = new DateOnly(2026, 7, 9);

        var result = TpexDailyCloseParser.Parse(json);

        Assert.NotNull(result.SourceDataDate);
        Assert.NotEqual(requestedDate, result.SourceDataDate);
        Assert.Equal(new DateOnly(2026, 7, 8), result.SourceDataDate);
    }

    [Fact]
    public void HTTP200但為錯誤頁或非JSON時應拋出可重試例外()
    {
        Assert.Throws<RetryableJobException>(() => TpexDailyCloseParser.Parse("<html>錯誤頁</html>"));
    }

    [Fact]
    public void 沒有date也沒有tables時視為明確查無資料()
    {
        const string json = "{}";
        var result = TpexDailyCloseParser.Parse(json);
        Assert.True(result.IsExplicitNoData);
        Assert.Empty(result.Quotes);
    }

    [Fact]
    public void 有來源日期但totalCount為0且data空時視為明確查無資料()
    {
        const string json = """
            {"tables":[{"date":"115/07/10","totalCount":0,"fields":["代號","名稱","收盤 "],"data":[]}]}
            """;

        var result = TpexDailyCloseParser.Parse(json);

        Assert.Equal(new DateOnly(2026, 7, 10), result.SourceDataDate);
        Assert.True(result.IsExplicitNoData);
        Assert.Empty(result.Quotes);
    }

    [Fact]
    public void 缺少欄位定義時應拋出可重試例外()
    {
        const string json = """{"date":"20260708","tables":[{"fields":["其他欄位"],"data":[]}]}""";
        Assert.Throws<RetryableJobException>(() => TpexDailyCloseParser.Parse(json));
    }
}
