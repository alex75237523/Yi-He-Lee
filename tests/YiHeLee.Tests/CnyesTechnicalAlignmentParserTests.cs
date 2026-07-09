using YiHeLee.Application.Exceptions;
using YiHeLee.Domain;
using YiHeLee.Infrastructure.Crawlers;

namespace YiHeLee.Tests;

public sealed class CnyesTechnicalAlignmentParserTests
{
    private readonly CnyesTechnicalAlignmentParser _parser = new();

    [Fact]
    public void 可解析完整技術指標表格()
    {
        var source = SourceDefinitionSetting.CreateDefaults()[0].ToDomain();
        List<string[]> rows =
        [
            ["代碼", "名稱", "收盤價", "5日均價", "20日均價", "60日均價", "120日均價"],
            ["5285", "宜鼎", "1,515", "1,490.5", "1,420", "1,300", "1,100"]
        ];
        var now = new DateTimeOffset(2026, 7, 9, 13, 35, 0, TimeSpan.FromHours(8));

        var result = _parser.ParseRows(rows, source, MarketType.Listed, new DateOnly(2026, 7, 9), now, now);

        var item = Assert.Single(result);
        Assert.Equal("5285", item.StockCode);
        Assert.Equal(1515m, item.ClosePrice);
        Assert.Equal(1490.5m, item.MovingAverage5);
        Assert.Equal(MarketType.Listed, item.MarketType);
    }

    [Fact]
    public void 空頭頁股票名稱表頭也可解析()
    {
        var source = SourceDefinitionSetting.CreateDefaults()[1].ToDomain();
        List<string[]> rows =
        [
            ["代碼", "股票名稱", "收盤價", "5日均價", "20日均價", "60日均價", "120日均價"],
            ["1101", "台泥", "23.10", "23.39", "24.04", "24.35", "24.62"]
        ];
        var now = new DateTimeOffset(2026, 7, 9, 13, 35, 0, TimeSpan.FromHours(8));

        var result = _parser.ParseRows(rows, source, MarketType.Listed, new DateOnly(2026, 7, 9), now, now);

        Assert.Equal("台泥", Assert.Single(result).StockName);
    }

    [Fact]
    public void 缺少必要表頭_必須失敗()
    {
        var source = SourceDefinitionSetting.CreateDefaults()[1].ToDomain();
        List<string[]> rows = [["代碼", "名稱", "收盤價"]];
        var now = DateTimeOffset.Now;

        Assert.Throws<RetryableJobException>(() =>
            _parser.ParseRows(rows, source, MarketType.Otc, new DateOnly(2026, 7, 9), now, now));
    }

    [Fact]
    public void 取得表格附近最後一個頁面日期()
    {
        var result = _parser.ExtractDisplayedDate("查詢日期 2026-07-08 目前資料日期 2026-07-09");

        Assert.Equal(new DateOnly(2026, 7, 9), result);
    }
}
