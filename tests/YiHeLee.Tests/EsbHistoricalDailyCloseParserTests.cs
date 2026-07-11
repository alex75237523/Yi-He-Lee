using YiHeLee.Application.Exceptions;
using YiHeLee.Infrastructure.MarketData;

namespace YiHeLee.Tests;

public sealed class EsbHistoricalDailyCloseParserTests
{
    [Fact]
    public void 可解析興櫃個股歷史月資料並以成交均價作為價格()
    {
        const string json = """
            {
              "tables": [
                {
                  "title": "興櫃個股歷史行情",
                  "date": "115年07月",
                  "totalCount": 2,
                  "fields": ["日期","成交股數","成交金額(元)","成交最高","成交最低","成交均價","筆數","成交股數","成交金額(元)","成交最高","成交最低","成交均價","筆數"],
                  "data": [
                    ["115/07/01","24,500","589,425","24.70","23.55","24.06","15","0","0","0.00","0.00","0.00","0"],
                    ["115/07/02","7,200","178,920","25.40","24.25","24.85","9","0","0","0.00","0.00","0.00","0"]
                  ],
                  "subtitle": "115年07月 1260 富味鄉",
                  "notes": []
                }
              ],
              "date": "20260711"
            }
            """;

        var result = EsbHistoricalDailyCloseParser.Parse(json, "1260");

        Assert.Equal(2, result.Quotes.Count);
        var quote = Assert.Single(result.Quotes, x => x.TradeDate == new DateOnly(2026, 7, 2));
        Assert.Equal("1260", quote.Quote.StockCode);
        Assert.Equal("富味鄉", quote.Quote.StockName);
        Assert.Equal(24.85m, quote.Quote.ClosePrice);
    }

    [Fact]
    public void 查無資料時回傳空集合()
    {
        const string json = """
            {
              "stat": "查無股票代碼1260於115年06月之歷史資料",
              "tables": [
                {
                  "title": "興櫃個股歷史行情",
                  "data": [],
                  "date": "115年06月",
                  "totalCount": 0,
                  "fields": ["日期","成交均價"],
                  "subtitle": "115年06月 1260 富味鄉",
                  "notes": []
                }
              ],
              "date": "20260711"
            }
            """;

        var result = EsbHistoricalDailyCloseParser.Parse(json, "1260");

        Assert.Empty(result.Quotes);
    }

    [Fact]
    public void 缺少成交均價欄位時拋出可重試例外()
    {
        const string json = """
            {"tables":[{"fields":["日期"],"data":[],"subtitle":"115年07月 1260 富味鄉"}]}
            """;

        Assert.Throws<RetryableJobException>(() => EsbHistoricalDailyCloseParser.Parse(json, "1260"));
    }
}
