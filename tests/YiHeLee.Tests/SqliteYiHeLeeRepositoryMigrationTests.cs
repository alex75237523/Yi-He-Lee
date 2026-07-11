using Microsoft.Data.Sqlite;
using YiHeLee.Application.Abstractions;
using YiHeLee.Domain;
using YiHeLee.Infrastructure.Data;

namespace YiHeLee.Tests;

/// <summary>
/// 2026-07-11 需求更正：策略比較基準由「進場價／平均價」改為 Excel「現價」欄位（DDE）。
/// 驗證舊資料庫的 EntryAveragePrice 欄位會自動遷移為可為 NULL 的 CurrentPrice，
/// 且遷移後可正常寫入「現價無效」（CurrentPrice 為 NULL）的通知列。
/// </summary>
public sealed class SqliteYiHeLeeRepositoryMigrationTests : IDisposable
{
    private static readonly DateOnly TradeDate = new(2026, 7, 11);
    private readonly string _databasePath;

    public SqliteYiHeLeeRepositoryMigrationTests()
    {
        _databasePath = Path.Combine(Path.GetTempPath(), $"yihelee-migration-test-{Guid.NewGuid():N}.db");
    }

    [Fact]
    public async Task 舊資料庫的進場價欄位自動改名為現價且保留舊值()
    {
        CreateLegacySchemaWithOneRowEach();

        var repository = new SqliteYiHeLeeRepository(_databasePath, new FixedClock());
        await repository.InitializeAsync();

        await using var connection = new SqliteConnection($"Data Source={_databasePath}");
        await connection.OpenAsync();

        foreach (var table in new[] { "CustomerHoldingSnapshots", "StrategyAlerts" })
        {
            Assert.False(await ColumnExistsAsync(connection, table, "EntryAveragePrice"));
            Assert.True(await ColumnExistsAsync(connection, table, "CurrentPrice"));
        }

        Assert.Equal(123.5m, Convert.ToDecimal(await ScalarAsync(connection, "SELECT CurrentPrice FROM CustomerHoldingSnapshots;")));
        Assert.Equal(123.5m, Convert.ToDecimal(await ScalarAsync(connection, "SELECT CurrentPrice FROM StrategyAlerts;")));
        Assert.Equal(1, Convert.ToInt32(await ScalarAsync(connection, "SELECT COUNT(*) FROM CustomerHoldingSnapshots_Legacy_Check;")));
    }

    [Fact]
    public async Task 遷移後可寫入現價無效的持股與通知()
    {
        CreateLegacySchemaWithOneRowEach();

        var repository = new SqliteYiHeLeeRepository(_databasePath, new FixedClock());
        await repository.InitializeAsync();

        var jobId = await repository.BeginJobAsync(TradeDate, 1, Now(), CancellationToken.None);
        var holding = new CustomerHolding(
            TradeDate, @"C:\Data\親帶績效.xlsx", "王保仁-A", "王保仁", 4, "5285", "宜鼎",
            null, 8, @"C:\DATA\親帶績效.XLSX|王保仁-A|4|5285", "儲存格為 #N/A（DDE 尚未取得資料，看盤軟體可能未開啟或未連線）");
        var alert = new StrategyAlert(
            TradeDate, AlertKind.CurrentPriceInvalid, @"C:\Data\親帶績效.xlsx", "王保仁-A", "王保仁", 4, "5285", "宜鼎",
            null, 8, null, null, null, null, null, false, false, false,
            "現價無效，無法判斷：儲存格為 #N/A（DDE 尚未取得資料，看盤軟體可能未開啟或未連線）。",
            MarketType.Otc, null, null, "TPEx", Now());

        await repository.SaveHoldingsAndAlertsAsync(jobId, TradeDate, @"C:\Data\親帶績效.xlsx", [holding], [alert], CancellationToken.None);

        await using var connection = new SqliteConnection($"Data Source={_databasePath}");
        await connection.OpenAsync();
        Assert.Equal(DBNull.Value, await ScalarAsync(connection, "SELECT CurrentPrice FROM StrategyAlerts WHERE AlertKind = 3;"));
        Assert.Equal(1, Convert.ToInt32(await ScalarAsync(connection, "SELECT COUNT(*) FROM CustomerHoldingSnapshots WHERE CurrentPrice IS NULL;")));
    }

    /// <summary>建立 2026-07-11 之前的舊結構（EntryAveragePrice NOT NULL）並各塞一筆資料。</summary>
    private void CreateLegacySchemaWithOneRowEach()
    {
        using var connection = new SqliteConnection($"Data Source={_databasePath}");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            CREATE TABLE JobRuns (
                JobId TEXT NOT NULL PRIMARY KEY,
                TargetDate TEXT NOT NULL,
                TaipeiStartedAt TEXT NOT NULL,
                TaipeiCompletedAt TEXT NULL,
                AttemptNumber INTEGER NOT NULL,
                Status INTEGER NOT NULL,
                Outcome INTEGER NULL,
                Message TEXT NULL,
                CrawledCount INTEGER NOT NULL DEFAULT 0,
                HoldingCount INTEGER NOT NULL DEFAULT 0,
                AlertCount INTEGER NOT NULL DEFAULT 0,
                MissingIndicatorCount INTEGER NOT NULL DEFAULT 0
            );
            CREATE TABLE CustomerHoldingSnapshots (
                Id INTEGER NOT NULL PRIMARY KEY AUTOINCREMENT,
                JobId TEXT NOT NULL,
                SnapshotDate TEXT NOT NULL,
                WorkbookPath TEXT NOT NULL,
                SheetName TEXT NOT NULL,
                CustomerName TEXT NOT NULL,
                ExcelRow INTEGER NOT NULL,
                StockCode TEXT NOT NULL,
                StockName TEXT NOT NULL,
                EntryAveragePrice NUMERIC NOT NULL,
                Quantity NUMERIC NULL,
                HoldingKey TEXT NOT NULL,
                CreatedAt TEXT NOT NULL,
                CONSTRAINT UQ_CustomerHoldingSnapshots UNIQUE (SnapshotDate, HoldingKey)
            );
            CREATE TABLE StrategyAlerts (
                Id INTEGER NOT NULL PRIMARY KEY AUTOINCREMENT,
                JobId TEXT NOT NULL,
                TradeDate TEXT NOT NULL,
                AlertKind INTEGER NOT NULL,
                WorkbookPath TEXT NOT NULL,
                SheetName TEXT NOT NULL,
                CustomerName TEXT NOT NULL,
                ExcelRow INTEGER NOT NULL,
                StockCode TEXT NOT NULL,
                StockName TEXT NOT NULL,
                EntryAveragePrice NUMERIC NOT NULL,
                Quantity NUMERIC NULL,
                ClosePrice NUMERIC NULL,
                MovingAverage5 NUMERIC NULL,
                MovingAverage20 NUMERIC NULL,
                MovingAverage60 NUMERIC NULL,
                MovingAverage120 NUMERIC NULL,
                TriggeredMa5 INTEGER NOT NULL,
                TriggeredMa20 INTEGER NOT NULL,
                TriggeredMa120 INTEGER NOT NULL,
                TriggerDescription TEXT NOT NULL,
                MarketType INTEGER NULL,
                IndicatorType INTEGER NULL,
                SourceUrl TEXT NULL,
                CreatedAt TEXT NOT NULL,
                UpdatedAt TEXT NOT NULL,
                CONSTRAINT UQ_StrategyAlerts UNIQUE (TradeDate, WorkbookPath, SheetName, ExcelRow, StockCode)
            );
            INSERT INTO JobRuns (JobId, TargetDate, TaipeiStartedAt, AttemptNumber, Status)
            VALUES ('legacy-job', '2026-07-10', '2026-07-10T13:35:00+08:00', 1, 2);
            INSERT INTO CustomerHoldingSnapshots
                (JobId, SnapshotDate, WorkbookPath, SheetName, CustomerName, ExcelRow, StockCode, StockName, EntryAveragePrice, Quantity, HoldingKey, CreatedAt)
            VALUES ('legacy-job', '2026-07-10', 'C:\Data\legacy.xlsx', '客戶A', '客戶A', 4, '5285', '宜鼎', 123.5, 8, 'KEY-1', '2026-07-10T13:35:00+08:00');
            INSERT INTO StrategyAlerts
                (JobId, TradeDate, AlertKind, WorkbookPath, SheetName, CustomerName, ExcelRow, StockCode, StockName, EntryAveragePrice, Quantity,
                 TriggeredMa5, TriggeredMa20, TriggeredMa120, TriggerDescription, CreatedAt, UpdatedAt)
            VALUES ('legacy-job', '2026-07-10', 1, 'C:\Data\legacy.xlsx', '客戶A', '客戶A', 4, '5285', '宜鼎', 123.5, 8,
                    1, 0, 0, '舊資料', '2026-07-10T13:35:00+08:00', '2026-07-10T13:35:00+08:00');
            -- 供測試確認遷移不影響其他既有資料表。
            CREATE TABLE CustomerHoldingSnapshots_Legacy_Check (Id INTEGER PRIMARY KEY);
            INSERT INTO CustomerHoldingSnapshots_Legacy_Check (Id) VALUES (1);
            """;
        command.ExecuteNonQuery();
    }

    private static async Task<bool> ColumnExistsAsync(SqliteConnection connection, string table, string column)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM pragma_table_info($table) WHERE name = $column;";
        command.Parameters.AddWithValue("$table", table);
        command.Parameters.AddWithValue("$column", column);
        return Convert.ToInt32(await command.ExecuteScalarAsync()) > 0;
    }

    private static async Task<object?> ScalarAsync(SqliteConnection connection, string sql)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        return await command.ExecuteScalarAsync();
    }

    private static DateTimeOffset Now() => new(TradeDate.ToDateTime(new TimeOnly(13, 35)), TimeSpan.FromHours(8));

    private sealed class FixedClock : IClock
    {
        public DateTimeOffset GetTaipeiNow() => Now();
        public DateOnly GetTaipeiToday() => TradeDate;
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try { File.Delete(_databasePath); } catch { /* 測試結束清理，失敗不影響結果 */ }
    }
}
