using Microsoft.Data.Sqlite;
using YiHeLee.Application.Abstractions;
using YiHeLee.Application.Services;
using YiHeLee.Domain;
using YiHeLee.Infrastructure.Data;

namespace YiHeLee.Tests;

/// <summary>
/// 歷史收盤價分頁查詢（SQL Window Function 計算 MA5／20／60／120）相關測試，
/// 驗證與既有 <see cref="MovingAverageService"/>（Rolling Sum／逐檔查詢）結果一致，
/// 且分頁、篩選、排序皆正確，不會一次載入全部歷史資料。
/// </summary>
public sealed class SqliteMarketDataRepositoryQueryTests : IDisposable
{
    private static readonly DateOnly Latest = new(2026, 7, 9);
    private readonly string _databasePath;
    private readonly SqliteMarketDataRepository _repository;

    public SqliteMarketDataRepositoryQueryTests()
    {
        _databasePath = Path.Combine(Path.GetTempPath(), $"yihelee-query-test-{Guid.NewGuid():N}.db");
        _repository = new SqliteMarketDataRepository(_databasePath, new FixedClock());
        _repository.InitializeAsync().GetAwaiter().GetResult();
    }

    [Fact]
    public async Task SQLWindowFunction計算之MA與直接平均結果一致()
    {
        await SeedHistoryAsync("5285", "宜鼎", MarketType.Otc, days: 130);

        var result = await _repository.QueryDailyPricesAsync(
            new StockDailyPriceQueryFilter(MarketScope.All, "5285", null, null, 1, 10),
            CancellationToken.None);

        var latestRow = Assert.Single(result.Rows, r => r.TradeDate == Latest);
        // 與 MovingAverageServiceTests 相同的人工驗算資料集：收盤價 = 100 + i（i=0 為最新一日）。
        Assert.Equal(100m, latestRow.ClosePrice);
        Assert.Equal(102m, latestRow.MovingAverage5);   // (100+101+102+103+104)/5
        Assert.Equal(109.5m, latestRow.MovingAverage20);
        Assert.Equal(129.5m, latestRow.MovingAverage60);
        Assert.Equal(159.5m, latestRow.MovingAverage120);
    }

    [Fact]
    public async Task 資料不足時MA欄位為Null而非0()
    {
        await SeedHistoryAsync("5285", "宜鼎", MarketType.Otc, days: 3);

        var result = await _repository.QueryDailyPricesAsync(
            new StockDailyPriceQueryFilter(MarketScope.All, "5285", null, null, 1, 10),
            CancellationToken.None);

        var latestRow = Assert.Single(result.Rows, r => r.TradeDate == Latest);
        Assert.Null(latestRow.MovingAverage5);
        Assert.Null(latestRow.MovingAverage20);
        Assert.Null(latestRow.MovingAverage60);
        Assert.Null(latestRow.MovingAverage120);
    }

    [Fact]
    public async Task 依市場別篩選僅回傳指定市場資料()
    {
        await SeedHistoryAsync("5285", "宜鼎", MarketType.Otc, days: 5);
        await SeedHistoryAsync("2330", "台積電", MarketType.Listed, days: 5);

        var result = await _repository.QueryDailyPricesAsync(
            new StockDailyPriceQueryFilter(MarketScope.Listed, null, null, null, 1, 50),
            CancellationToken.None);

        Assert.All(result.Rows, r => Assert.Equal(MarketType.Listed, r.MarketType));
        Assert.Contains(result.Rows, r => r.StockCode == "2330");
        Assert.DoesNotContain(result.Rows, r => r.StockCode == "5285");
    }

    [Fact]
    public async Task 依股票代碼或名稱關鍵字搜尋()
    {
        await SeedHistoryAsync("5285", "宜鼎", MarketType.Otc, days: 1);
        await SeedHistoryAsync("2330", "台積電", MarketType.Listed, days: 1);

        var byCode = await _repository.QueryDailyPricesAsync(
            new StockDailyPriceQueryFilter(MarketScope.All, "2330", null, null, 1, 50), CancellationToken.None);
        Assert.Single(byCode.Rows);
        Assert.Equal("2330", byCode.Rows[0].StockCode);

        var byName = await _repository.QueryDailyPricesAsync(
            new StockDailyPriceQueryFilter(MarketScope.All, "台積", null, null, 1, 50), CancellationToken.None);
        Assert.Single(byName.Rows);
        Assert.Equal("2330", byName.Rows[0].StockCode);
    }

    [Fact]
    public async Task 依日期區間篩選()
    {
        await SeedHistoryAsync("5285", "宜鼎", MarketType.Otc, days: 10);

        var result = await _repository.QueryDailyPricesAsync(
            new StockDailyPriceQueryFilter(MarketScope.All, null, Latest.AddDays(-2), Latest, 1, 50),
            CancellationToken.None);

        Assert.All(result.Rows, r => Assert.True(r.TradeDate >= Latest.AddDays(-2) && r.TradeDate <= Latest));
    }

    [Fact]
    public async Task 分頁正確且不會一次回傳全部資料()
    {
        await SeedHistoryAsync("5285", "宜鼎", MarketType.Otc, days: 25);

        var page1 = await _repository.QueryDailyPricesAsync(
            new StockDailyPriceQueryFilter(MarketScope.All, null, null, null, 1, 10), CancellationToken.None);
        var page2 = await _repository.QueryDailyPricesAsync(
            new StockDailyPriceQueryFilter(MarketScope.All, null, null, null, 2, 10), CancellationToken.None);
        var page3 = await _repository.QueryDailyPricesAsync(
            new StockDailyPriceQueryFilter(MarketScope.All, null, null, null, 3, 10), CancellationToken.None);

        Assert.Equal(25, page1.TotalCount);
        Assert.Equal(10, page1.Rows.Count);
        Assert.Equal(10, page2.Rows.Count);
        Assert.Equal(5, page3.Rows.Count);
        Assert.True(page1.Rows[0].TradeDate > page1.Rows[^1].TradeDate); // 由新到舊排序
        Assert.DoesNotContain(page2.Rows, r => page1.Rows.Any(p => p.TradeDate == r.TradeDate));
    }

    [Fact]
    public async Task 尚無資料時取得最新交易日期為Null_寫入後回傳正確日期()
    {
        Assert.Null(await _repository.GetLatestTradeDateAsync(CancellationToken.None));

        await SeedHistoryAsync("5285", "宜鼎", MarketType.Otc, days: 3);

        Assert.Equal(Latest, await _repository.GetLatestTradeDateAsync(CancellationToken.None));
    }

    private async Task SeedHistoryAsync(string code, string name, MarketType marketType, int days)
    {
        for (var i = 0; i < days; i++)
        {
            var tradeDate = Latest.AddDays(-i);
            var price = new OfficialStockPrice(
                code, name, marketType, tradeDate, 100m + i,
                marketType == MarketType.Listed ? "TWSE" : "TPEx",
                "https://example.invalid/",
                tradeDate, "batch", new DateTimeOffset(tradeDate.ToDateTime(new TimeOnly(14, 0)), TimeSpan.FromHours(8)));
            await _repository.UpsertDailyPricesAsync([price], CancellationToken.None);
        }
    }

    private sealed class FixedClock : IClock
    {
        public DateTimeOffset GetTaipeiNow() => new(Latest.ToDateTime(new TimeOnly(13, 35)), TimeSpan.FromHours(8));
        public DateOnly GetTaipeiToday() => Latest;
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try { File.Delete(_databasePath); } catch { /* 測試結束清理，失敗不影響結果 */ }
    }
}
