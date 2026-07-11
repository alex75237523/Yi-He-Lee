using Microsoft.Data.Sqlite;
using YiHeLee.Application.Abstractions;
using YiHeLee.Domain;
using YiHeLee.Infrastructure.Data;

namespace YiHeLee.Tests;

/// <summary>
/// 驗證 StockMovingAverage 新增的逐檔歷史完整性診斷欄位（LatestAvailableTradeDate／MissingReason）
/// 可安全遷移舊資料庫（欄位不存在時以 ALTER TABLE ADD COLUMN 補上，不刪除或覆蓋既有資料列），
/// 且遷移後可正常讀寫。
/// </summary>
public sealed class SqliteMarketDataRepositoryMigrationTests : IDisposable
{
    private static readonly DateOnly TradeDate = new(2026, 7, 9);
    private readonly string _databasePath;

    public SqliteMarketDataRepositoryMigrationTests()
    {
        _databasePath = Path.Combine(Path.GetTempPath(), $"yihelee-marketdata-migration-test-{Guid.NewGuid():N}.db");
    }

    [Fact]
    public async Task 舊資料庫缺少新欄位時自動補上且保留既有資料列()
    {
        await CreateLegacySchemaWithOneRowAsync();

        var repository = new SqliteMarketDataRepository(_databasePath, new FixedClock());
        await repository.InitializeAsync();

        await using var connection = new SqliteConnection($"Data Source={_databasePath}");
        await connection.OpenAsync();

        Assert.True(await ColumnExistsAsync(connection, "StockMovingAverage", "LatestAvailableTradeDate"));
        Assert.True(await ColumnExistsAsync(connection, "StockMovingAverage", "MissingReason"));

        // 舊資料列必須原樣保留（新欄位補上後為 NULL），不得因遷移而遺失。
        Assert.Equal(1, Convert.ToInt32(await ScalarAsync(connection, "SELECT COUNT(*) FROM StockMovingAverage;")));
        Assert.Equal(88.5m, Convert.ToDecimal(await ScalarAsync(connection, "SELECT Ma5 FROM StockMovingAverage;")));
    }

    [Fact]
    public async Task 遷移後可寫入並讀回逐檔診斷欄位()
    {
        await CreateLegacySchemaWithOneRowAsync();

        var repository = new SqliteMarketDataRepository(_databasePath, new FixedClock());
        await repository.InitializeAsync();

        var result = new MovingAverageResult(
            "5351", TradeDate, 90m, 88m, null, null, null, 83,
            CalculationStatus.InsufficientHistory, TradeDate,
            "僅累積 83 個有效交易日，MA120 尚缺 37 個有效交易日（逐檔檢查，非市場整體交易日數）。");

        // 先寫入官方收盤價，讓 StockMaster／StockId 存在，均線快取才能成功關聯。
        await repository.UpsertDailyPricesAsync(
            [new OfficialStockPrice("5351", "鈺創", MarketType.Otc, TradeDate, 90m, "TPEx", "https://example.invalid", TradeDate, "batch-1", DateTimeOffset.UtcNow)],
            CancellationToken.None);
        await repository.SaveMovingAverageResultsAsync(TradeDate, [result], CancellationToken.None);

        var loaded = await repository.GetMovingAverageResultsAsync(TradeDate, CancellationToken.None);

        var loadedResult = Assert.Single(loaded, x => x.StockCode == "5351");
        Assert.Equal(83, loadedResult.AvailableTradingDayCount);
        Assert.Equal(TradeDate, loadedResult.LatestAvailableTradeDate);
        Assert.Contains("37 個有效交易日", loadedResult.MissingReason);
        Assert.Equal(CalculationStatus.InsufficientHistory, loadedResult.CalculationStatus);
    }

    /// <summary>建立本次新增欄位（LatestAvailableTradeDate／MissingReason）之前的舊版 StockMovingAverage 結構。</summary>
    private async Task CreateLegacySchemaWithOneRowAsync()
    {
        await using var connection = new SqliteConnection($"Data Source={_databasePath}");
        await connection.OpenAsync();

        const string legacySchema = """
            CREATE TABLE StockMaster (
                Id INTEGER NOT NULL PRIMARY KEY AUTOINCREMENT,
                StockCode TEXT NOT NULL UNIQUE,
                StockName TEXT NOT NULL,
                MarketType INTEGER NOT NULL,
                SecurityType TEXT NOT NULL DEFAULT '一般',
                IsActive INTEGER NOT NULL DEFAULT 1,
                CreatedAt TEXT NOT NULL,
                UpdatedAt TEXT NOT NULL
            );

            CREATE TABLE StockMovingAverage (
                Id INTEGER NOT NULL PRIMARY KEY AUTOINCREMENT,
                StockId INTEGER NOT NULL,
                TradeDate TEXT NOT NULL,
                ClosePrice NUMERIC NULL,
                Ma5 NUMERIC NULL,
                Ma20 NUMERIC NULL,
                Ma60 NUMERIC NULL,
                Ma120 NUMERIC NULL,
                AvailableTradingDayCount INTEGER NOT NULL,
                CalculationStatus INTEGER NOT NULL,
                CreatedAt TEXT NOT NULL,
                UpdatedAt TEXT NOT NULL,
                FOREIGN KEY (StockId) REFERENCES StockMaster(Id),
                CONSTRAINT UQ_StockMovingAverage UNIQUE (StockId, TradeDate)
            );
            """;

        await using (var command = connection.CreateCommand())
        {
            command.CommandText = legacySchema;
            await command.ExecuteNonQueryAsync();
        }

        var now = DateTimeOffset.UtcNow.ToString("O");
        await using (var insertStock = connection.CreateCommand())
        {
            insertStock.CommandText = """
                INSERT INTO StockMaster (StockCode, StockName, MarketType, CreatedAt, UpdatedAt)
                VALUES ('5351', '鈺創', 2, $now, $now);
                """;
            insertStock.Parameters.AddWithValue("$now", now);
            await insertStock.ExecuteNonQueryAsync();
        }

        await using (var insertMa = connection.CreateCommand())
        {
            insertMa.CommandText = """
                INSERT INTO StockMovingAverage (StockId, TradeDate, ClosePrice, Ma5, Ma20, Ma60, Ma120, AvailableTradingDayCount, CalculationStatus, CreatedAt, UpdatedAt)
                VALUES (1, $tradeDate, 90.0, 88.5, NULL, NULL, NULL, 83, 2, $now, $now);
                """;
            insertMa.Parameters.AddWithValue("$tradeDate", TradeDate.ToString("yyyy-MM-dd"));
            insertMa.Parameters.AddWithValue("$now", now);
            await insertMa.ExecuteNonQueryAsync();
        }
    }

    private static async Task<bool> ColumnExistsAsync(SqliteConnection connection, string table, string column)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = $"PRAGMA table_info({table});";
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            if (string.Equals(reader.GetString(1), column, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private static async Task<object?> ScalarAsync(SqliteConnection connection, string sql)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        return await command.ExecuteScalarAsync();
    }

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
