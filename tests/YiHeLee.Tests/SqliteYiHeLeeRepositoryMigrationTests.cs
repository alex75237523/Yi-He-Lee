using Microsoft.Data.Sqlite;
using YiHeLee.Application.Abstractions;
using YiHeLee.Domain;
using YiHeLee.Infrastructure.Data;

namespace YiHeLee.Tests;

/// <summary>
/// 2026-07-09 修正：策略比較基準曾一度改為 Excel「現價」欄位（DDE）；舊資料庫的 EntryAveragePrice
/// 欄位會自動遷移為可為 NULL 的 CurrentPrice，且遷移後可正常寫入「現價無效」（CurrentPrice 為 NULL）
/// 的通知列。2026-07-11 正式恢復雙價格判斷後，「進場價/平均價」以全新獨立欄位
/// （同樣命名為 EntryAveragePrice，但與舊版歷史欄位無關）重新出現；本測試檔案同時驗證：
/// (1) 舊版歷史搬移邏輯不會被新版同名欄位誤觸發而覆蓋 CurrentPrice；
/// (2) 新版 EntryAveragePrice／EntryAveragePriceIssue／CurrentPriceIssue 欄位可安全新增、
///     分別保存讀回且互不覆蓋；(3) StrategyAlerts 唯一鍵新增 AlertKind 後，同一持股可同時保存
///     「進場價/平均價異常」與「現價異常」兩筆通知而不互相覆蓋。
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
            Assert.True(await ColumnExistsAsync(connection, table, "CurrentPrice"));
            // 2026-07-11 恢復雙價格判斷後，EntryAveragePrice 是全新獨立欄位（與舊版同名歷史欄位無關），
            // 必須存在，但舊資料列搬移時沒有對應資料，不得誤用舊版「進場價」欄位的歷史數值填入，須為 NULL。
            Assert.True(await ColumnExistsAsync(connection, table, "EntryAveragePrice"));
        }

        Assert.Equal(123.5m, Convert.ToDecimal(await ScalarAsync(connection, "SELECT CurrentPrice FROM CustomerHoldingSnapshots;")));
        Assert.Equal(123.5m, Convert.ToDecimal(await ScalarAsync(connection, "SELECT CurrentPrice FROM StrategyAlerts;")));
        Assert.Equal(DBNull.Value, await ScalarAsync(connection, "SELECT EntryAveragePrice FROM CustomerHoldingSnapshots;"));
        Assert.Equal(DBNull.Value, await ScalarAsync(connection, "SELECT EntryAveragePrice FROM StrategyAlerts;"));
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

    [Fact]
    public async Task 舊資料庫缺少CurrentPriceIssue欄位時自動安全補上且保留既有資料列()
    {
        CreateSchemaWithCurrentPriceButNoIssueColumn();

        var repository = new SqliteYiHeLeeRepository(_databasePath, new FixedClock());
        await repository.InitializeAsync();

        await using var connection = new SqliteConnection($"Data Source={_databasePath}");
        await connection.OpenAsync();

        Assert.True(await ColumnExistsAsync(connection, "CustomerHoldingSnapshots", "CurrentPriceIssue"));
        // 既有資料列（遷移前已存在）必須保留，不得因新增欄位而遺失。
        Assert.Equal(1, Convert.ToInt32(await ScalarAsync(connection, "SELECT COUNT(*) FROM CustomerHoldingSnapshots WHERE StockCode = '5285';")));

        // 遷移後可正常寫入並讀回 DDE 錯誤原因。
        var jobId = await repository.BeginJobAsync(TradeDate, 1, Now(), CancellationToken.None);
        var holding = new CustomerHolding(
            TradeDate, @"C:\Data\親帶績效.xlsx", "王保仁-A", "王保仁", 5, "5351", "鈺創",
            null, 8, @"C:\DATA\親帶績效.XLSX|王保仁-A|5|5351", "#N/A（DDE 尚未取得資料，看盤軟體可能未開啟或未連線）");
        await repository.SaveHoldingsAndAlertsAsync(jobId, TradeDate, @"C:\Data\親帶績效.xlsx", [holding], [], CancellationToken.None);

        var savedIssue = await ScalarAsync(connection, "SELECT CurrentPriceIssue FROM CustomerHoldingSnapshots WHERE StockCode = '5351';");
        Assert.Equal("#N/A（DDE 尚未取得資料，看盤軟體可能未開啟或未連線）", savedIssue);
    }

    [Fact]
    public async Task 舊資料庫可安全新增進場價平均價欄位且保留既有資料列()
    {
        CreateSchemaWithCurrentPriceButNoIssueColumn();

        var repository = new SqliteYiHeLeeRepository(_databasePath, new FixedClock());
        await repository.InitializeAsync();

        await using var connection = new SqliteConnection($"Data Source={_databasePath}");
        await connection.OpenAsync();

        Assert.True(await ColumnExistsAsync(connection, "CustomerHoldingSnapshots", "EntryAveragePrice"));
        Assert.True(await ColumnExistsAsync(connection, "CustomerHoldingSnapshots", "EntryAveragePriceIssue"));
        Assert.True(await ColumnExistsAsync(connection, "StrategyAlerts", "EntryAveragePrice"));
        Assert.True(await ColumnExistsAsync(connection, "StrategyAlerts", "EntryAveragePriceIssue"));
        Assert.True(await ColumnExistsAsync(connection, "StrategyAlerts", "CurrentPriceIssue"));
        // 既有資料列（遷移前已存在）必須保留，不得因新增欄位或重建資料表而遺失。
        Assert.Equal(1, Convert.ToInt32(await ScalarAsync(connection, "SELECT COUNT(*) FROM CustomerHoldingSnapshots WHERE StockCode = '5285';")));
    }

    [Fact]
    public async Task 進場價與現價可分別保存及讀回_原因不會互相覆蓋()
    {
        var repository = new SqliteYiHeLeeRepository(_databasePath, new FixedClock());
        await repository.InitializeAsync();

        var jobId = await repository.BeginJobAsync(TradeDate, 1, Now(), CancellationToken.None);
        var holding = new CustomerHolding(
            TradeDate, @"C:\Data\親帶績效.xlsx", "王保仁-A", "王保仁", 4, "5285", "宜鼎",
            520m, 8, @"C:\DATA\親帶績效.XLSX|王保仁-A|4|5285",
            CurrentPriceIssue: null,
            EntryAveragePrice: 501m,
            EntryAveragePriceIssue: null);

        await repository.SaveHoldingsAndAlertsAsync(jobId, TradeDate, @"C:\Data\親帶績效.xlsx", [holding], [], CancellationToken.None);

        await using var connection = new SqliteConnection($"Data Source={_databasePath}");
        await connection.OpenAsync();
        Assert.Equal(520m, Convert.ToDecimal(await ScalarAsync(connection, "SELECT CurrentPrice FROM CustomerHoldingSnapshots WHERE StockCode = '5285';")));
        Assert.Equal(501m, Convert.ToDecimal(await ScalarAsync(connection, "SELECT EntryAveragePrice FROM CustomerHoldingSnapshots WHERE StockCode = '5285';")));
        Assert.Equal(DBNull.Value, await ScalarAsync(connection, "SELECT CurrentPriceIssue FROM CustomerHoldingSnapshots WHERE StockCode = '5285';"));
        Assert.Equal(DBNull.Value, await ScalarAsync(connection, "SELECT EntryAveragePriceIssue FROM CustomerHoldingSnapshots WHERE StockCode = '5285';"));
    }

    [Fact]
    public async Task 同一持股進場價與現價同時無效時_兩筆通知都能保存不互相覆蓋()
    {
        // StrategyAlerts 唯一鍵新增 AlertKind 前，(TradeDate, WorkbookPath, SheetName, ExcelRow, StockCode)
        // 相同的兩筆通知會互相覆蓋，導致其中一個異常原因遺失；本測試驗證修正後兩筆各自保存。
        var repository = new SqliteYiHeLeeRepository(_databasePath, new FixedClock());
        await repository.InitializeAsync();

        var jobId = await repository.BeginJobAsync(TradeDate, 1, Now(), CancellationToken.None);
        var holding = new CustomerHolding(
            TradeDate, @"C:\Data\親帶績效.xlsx", "王保仁-A", "王保仁", 4, "5285", "宜鼎",
            null, 8, @"C:\DATA\親帶績效.XLSX|王保仁-A|4|5285",
            CurrentPriceIssue: "儲存格為 #N/A（DDE 尚未取得資料，看盤軟體可能未開啟或未連線）",
            EntryAveragePrice: null,
            EntryAveragePriceIssue: "儲存格為空白，無法讀取進場價/平均價");

        var entryAlert = new StrategyAlert(
            TradeDate, AlertKind.EntryAveragePriceInvalid, @"C:\Data\親帶績效.xlsx", "王保仁-A", "王保仁", 4, "5285", "宜鼎",
            null, 8, null, null, null, null, null, false, false, false,
            "進場價/平均價無效，無法判斷：儲存格為空白，無法讀取進場價/平均價。",
            MarketType.Otc, null, null, "TPEx", Now(),
            EntryAveragePrice: null, EntryAveragePriceIssue: "儲存格為空白，無法讀取進場價/平均價", CurrentPriceIssue: null);
        var currentAlert = new StrategyAlert(
            TradeDate, AlertKind.CurrentPriceInvalid, @"C:\Data\親帶績效.xlsx", "王保仁-A", "王保仁", 4, "5285", "宜鼎",
            null, 8, null, null, null, null, null, false, false, false,
            "現價無效，無法判斷：儲存格為 #N/A（DDE 尚未取得資料，看盤軟體可能未開啟或未連線）。",
            MarketType.Otc, null, null, "TPEx", Now(),
            EntryAveragePrice: null, EntryAveragePriceIssue: null, CurrentPriceIssue: "儲存格為 #N/A（DDE 尚未取得資料，看盤軟體可能未開啟或未連線）");

        await repository.SaveHoldingsAndAlertsAsync(jobId, TradeDate, @"C:\Data\親帶績效.xlsx", [holding], [entryAlert, currentAlert], CancellationToken.None);

        await using var connection = new SqliteConnection($"Data Source={_databasePath}");
        await connection.OpenAsync();
        Assert.Equal(2, Convert.ToInt32(await ScalarAsync(connection, "SELECT COUNT(*) FROM StrategyAlerts WHERE StockCode = '5285';")));
        Assert.Equal(
            "儲存格為空白，無法讀取進場價/平均價",
            await ScalarAsync(connection, "SELECT EntryAveragePriceIssue FROM StrategyAlerts WHERE StockCode = '5285' AND AlertKind = 4;"));
        Assert.Equal(
            "儲存格為 #N/A（DDE 尚未取得資料，看盤軟體可能未開啟或未連線）",
            await ScalarAsync(connection, "SELECT CurrentPriceIssue FROM StrategyAlerts WHERE StockCode = '5285' AND AlertKind = 3;"));

        // CustomerHoldingSnapshots 只有一列，但兩個 Issue 欄位必須各自保存、互不覆蓋。
        Assert.Equal(
            "儲存格為空白，無法讀取進場價/平均價",
            await ScalarAsync(connection, "SELECT EntryAveragePriceIssue FROM CustomerHoldingSnapshots WHERE StockCode = '5285';"));
        Assert.Equal(
            "儲存格為 #N/A（DDE 尚未取得資料，看盤軟體可能未開啟或未連線）",
            await ScalarAsync(connection, "SELECT CurrentPriceIssue FROM CustomerHoldingSnapshots WHERE StockCode = '5285';"));
    }

    [Fact]
    public async Task 同一天重跑不會產生重複的持股快照或通知()
    {
        var repository = new SqliteYiHeLeeRepository(_databasePath, new FixedClock());
        await repository.InitializeAsync();

        var holding = new CustomerHolding(
            TradeDate, @"C:\Data\親帶績效.xlsx", "王保仁-A", "王保仁", 4, "5285", "宜鼎",
            470m, 8, @"C:\DATA\親帶績效.XLSX|王保仁-A|4|5285", null, 501m, null);
        // 2026-07-19 正式策略：進場價 501 > MA20 480、現價 470 < MA5 490 → TriggeredMa20／TriggeredMa5 皆成立，TriggeredMa120 固定 false。
        var alert = new StrategyAlert(
            TradeDate, AlertKind.MovingAverageTriggered, @"C:\Data\親帶績效.xlsx", "王保仁-A", "王保仁", 4, "5285", "宜鼎",
            470m, 8, 480m, 490m, 480m, 480m, 600m, true, true, false,
            "符合通知條件：進場價/平均價 501 > MA20 480；現價 470 < MA5 490。",
            MarketType.Otc, null, null, "TPEx", Now(),
            EntryAveragePrice: 501m, EntryAveragePriceIssue: null, CurrentPriceIssue: null);

        var jobId1 = await repository.BeginJobAsync(TradeDate, 1, Now(), CancellationToken.None);
        await repository.SaveHoldingsAndAlertsAsync(jobId1, TradeDate, @"C:\Data\親帶績效.xlsx", [holding], [alert], CancellationToken.None);
        var jobId2 = await repository.BeginJobAsync(TradeDate, 2, Now(), CancellationToken.None);
        await repository.SaveHoldingsAndAlertsAsync(jobId2, TradeDate, @"C:\Data\親帶績效.xlsx", [holding], [alert], CancellationToken.None);

        await using var connection = new SqliteConnection($"Data Source={_databasePath}");
        await connection.OpenAsync();
        Assert.Equal(1, Convert.ToInt32(await ScalarAsync(connection, "SELECT COUNT(*) FROM CustomerHoldingSnapshots WHERE StockCode = '5285';")));
        Assert.Equal(1, Convert.ToInt32(await ScalarAsync(connection, "SELECT COUNT(*) FROM StrategyAlerts WHERE StockCode = '5285';")));
    }

    /// <summary>建立已完成 EntryAveragePrice→CurrentPrice 遷移、但尚無 CurrentPriceIssue 欄位的資料庫並塞一筆資料。</summary>
    private void CreateSchemaWithCurrentPriceButNoIssueColumn()
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
                CurrentPrice NUMERIC NULL,
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
                CurrentPrice NUMERIC NULL,
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
            VALUES ('legacy-job-2', '2026-07-10', '2026-07-10T13:35:00+08:00', 1, 2);
            INSERT INTO CustomerHoldingSnapshots
                (JobId, SnapshotDate, WorkbookPath, SheetName, CustomerName, ExcelRow, StockCode, StockName, CurrentPrice, Quantity, HoldingKey, CreatedAt)
            VALUES ('legacy-job-2', '2026-07-10', 'C:\Data\legacy.xlsx', '客戶A', '客戶A', 4, '5285', '宜鼎', 123.5, 8, 'KEY-2', '2026-07-10T13:35:00+08:00');
            """;
        command.ExecuteNonQuery();
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
