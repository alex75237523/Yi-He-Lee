using Microsoft.Data.Sqlite;
using YiHeLee.Application.Abstractions;
using YiHeLee.Domain;
using YiHeLee.Infrastructure.Data;

namespace YiHeLee.Tests;

public sealed class SqliteMarketDataRepositoryTests : IDisposable
{
    private readonly string _databasePath;
    private readonly SqliteMarketDataRepository _repository;
    private static readonly DateOnly TradeDate = new(2026, 7, 9);

    public SqliteMarketDataRepositoryTests()
    {
        _databasePath = Path.Combine(Path.GetTempPath(), $"yihelee-marketdata-test-{Guid.NewGuid():N}.db");
        _repository = new SqliteMarketDataRepository(_databasePath, new FixedClock());
        _repository.InitializeAsync().GetAwaiter().GetResult();
    }

    [Fact]
    public async Task 同日重跑同一股票不新增重複列而是更新既有資料()
    {
        var first = CreatePrice("5285", "宜鼎", 515m, "batch-1");
        var (inserted1, updated1) = await _repository.UpsertDailyPricesAsync([first], CancellationToken.None);
        Assert.Equal(1, inserted1);
        Assert.Equal(0, updated1);

        var second = CreatePrice("5285", "宜鼎", 520m, "batch-2");
        var (inserted2, updated2) = await _repository.UpsertDailyPricesAsync([second], CancellationToken.None);
        Assert.Equal(0, inserted2);
        Assert.Equal(1, updated2);

        var history = await _repository.GetRecentClosePricesAsync("5285", TradeDate, 10, CancellationToken.None);
        var only = Assert.Single(history);
        Assert.Equal(520m, only.ClosePrice); // 應反映最新一次寫入的收盤價，而非重複兩筆
    }

    [Fact]
    public async Task 批次含不同交易日時整批拒絕不寫入任何資料()
    {
        var mixedBatch = new[]
        {
            CreatePrice("5285", "宜鼎", 515m, "batch-1"),
            CreatePrice("2330", "台積電", 900m, "batch-1") with { TradeDate = TradeDate.AddDays(-1) }
        };

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => _repository.UpsertDailyPricesAsync(mixedBatch, CancellationToken.None));

        var history = await _repository.GetRecentClosePricesAsync("5285", TradeDate, 10, CancellationToken.None);
        Assert.Empty(history);
    }

    [Fact]
    public async Task 批次中途失敗時已寫入部分應完整回滾_不留下半批資料()
    {
        var valid = CreatePrice("5285", "宜鼎", 515m, "batch-1");
        var invalid = CreatePrice("2330", "台積電", 900m, "batch-1") with { SourceUrl = null! };

        // Microsoft.Data.Sqlite 對未設定值的參數（null 而非 DBNull.Value）於繫結階段即拋出 InvalidOperationException；
        // 無論是繫結階段或資料庫層級的失敗，只要發生在交易中途，都必須驗證已寫入部分會完整回滾。
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => _repository.UpsertDailyPricesAsync([valid, invalid], CancellationToken.None));

        var history = await _repository.GetRecentClosePricesAsync("5285", TradeDate, 10, CancellationToken.None);
        Assert.Empty(history); // 即使批次中「5285」先寫入成功，交易失敗仍須整批回滾。
    }

    [Fact]
    public async Task 依交易日期由新到舊排序且受maxTradingDays限制()
    {
        for (var i = 0; i < 5; i++)
        {
            var price = CreatePrice("5285", "宜鼎", 500m + i, "batch") with { TradeDate = TradeDate.AddDays(-i) };
            await _repository.UpsertDailyPricesAsync([price], CancellationToken.None);
        }

        var history = await _repository.GetRecentClosePricesAsync("5285", TradeDate, 3, CancellationToken.None);

        Assert.Equal(3, history.Count);
        Assert.Equal(TradeDate, history[0].TradeDate);
        Assert.Equal(TradeDate.AddDays(-1), history[1].TradeDate);
        Assert.Equal(TradeDate.AddDays(-2), history[2].TradeDate);
    }

    [Fact]
    public async Task 股票主檔市場別可由官方價格寫入後查詢取得()
    {
        await _repository.UpsertDailyPricesAsync([CreatePrice("5285", "宜鼎", 515m, "batch-1")], CancellationToken.None);

        var marketType = await _repository.GetStockMarketTypeAsync("5285", CancellationToken.None);

        Assert.Equal(MarketType.Otc, marketType);
    }

    private static OfficialStockPrice CreatePrice(string code, string name, decimal close, string batchId) => new(
        code, name, MarketType.Otc, TradeDate, close, "TPEx",
        "https://www.tpex.org.tw/web/stock/aftertrading/daily_close_quotes/stk_quote_result.php",
        TradeDate, batchId, new DateTimeOffset(TradeDate.ToDateTime(new TimeOnly(14, 0)), TimeSpan.FromHours(8)));

    private sealed class FixedClock : IClock
    {
        public DateTimeOffset GetTaipeiNow() => new(TradeDate.ToDateTime(new TimeOnly(13, 35)), TimeSpan.FromHours(8));
        public DateOnly GetTaipeiToday() => TradeDate;
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try { File.Delete(_databasePath); } catch { /* 測試結束清理，失敗不影響結果 */ }
    }
}
