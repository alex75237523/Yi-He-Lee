using System.Globalization;
using Microsoft.Data.Sqlite;
using YiHeLee.Application.Abstractions;
using YiHeLee.Domain;

namespace YiHeLee.Infrastructure.Data;

/// <summary>
/// 盤中通知去重狀態（IntradayAlertState）與盤中執行紀錄（IntradayEvaluationRun）的 SQLite 實作
/// （2026-07-13 盤中／收盤流程拆分新增）。
/// 全部參數化 SQL；IntradayAlertState 以唯一鍵 Upsert；IntradayEvaluationRun 每分鐘一筆摘要。
/// Migration 只以 CREATE TABLE IF NOT EXISTS 新增兩張獨立資料表，不修改、不刪除既有
/// JobRuns／StrategyAlerts 等正式資料。
/// </summary>
public sealed class SqliteIntradayStateRepository : IIntradayStateRepository
{
    private readonly string _connectionString;
    private readonly IClock _clock;

    public SqliteIntradayStateRepository(string databasePath, IClock clock)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(databasePath)!);
        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Shared,
            ForeignKeys = true,
            Pooling = true
        }.ToString();
        _clock = clock;
    }

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await ExecuteNonQueryAsync(connection, "PRAGMA journal_mode=WAL;", cancellationToken).ConfigureAwait(false);
        await ExecuteNonQueryAsync(connection, SchemaSql, cancellationToken).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<IntradayAlertStateRecord>> GetAlertStatesAsync(
        DateOnly evaluationDate,
        string workbookPath,
        CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT EvaluationDate, BaselineTradeDate, WorkbookPath, SheetName, ExcelRow, StockCode,
                   AlertKind, MaWindow, IsActive, FirstTriggeredAt, LastEvaluatedAt, LastNotifiedAt, ClearedAt
            FROM IntradayAlertState
            WHERE EvaluationDate = $evaluationDate AND WorkbookPath = $workbookPath;
            """;

        await using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.Parameters.AddWithValue("$evaluationDate", ToDate(evaluationDate));
        command.Parameters.AddWithValue("$workbookPath", workbookPath);

        var result = new List<IntradayAlertStateRecord>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            result.Add(new IntradayAlertStateRecord(
                ParseDate(reader.GetString(0)),
                ParseDate(reader.GetString(1)),
                reader.GetString(2),
                reader.GetString(3),
                reader.GetInt32(4),
                reader.GetString(5),
                (AlertKind)reader.GetInt32(6),
                reader.GetInt32(7),
                reader.GetInt32(8) != 0,
                ParseTimestamp(reader.GetString(9)),
                ParseTimestamp(reader.GetString(10)),
                reader.IsDBNull(11) ? null : ParseTimestamp(reader.GetString(11)),
                reader.IsDBNull(12) ? null : ParseTimestamp(reader.GetString(12))));
        }

        return result;
    }

    public async Task UpsertAlertStatesAsync(
        IReadOnlyList<IntradayAlertStateRecord> states,
        CancellationToken cancellationToken)
    {
        if (states.Count == 0)
        {
            return;
        }

        const string sql = """
            INSERT INTO IntradayAlertState
                (EvaluationDate, BaselineTradeDate, WorkbookPath, SheetName, ExcelRow, StockCode,
                 AlertKind, MaWindow, IsActive, FirstTriggeredAt, LastEvaluatedAt, LastNotifiedAt, ClearedAt,
                 CreatedAt, UpdatedAt)
            VALUES
                ($evaluationDate, $baselineTradeDate, $workbookPath, $sheetName, $excelRow, $stockCode,
                 $alertKind, $maWindow, $isActive, $firstTriggeredAt, $lastEvaluatedAt, $lastNotifiedAt, $clearedAt,
                 $now, $now)
            ON CONFLICT (EvaluationDate, WorkbookPath, SheetName, ExcelRow, StockCode, AlertKind, MaWindow)
            DO UPDATE SET
                BaselineTradeDate = excluded.BaselineTradeDate,
                IsActive = excluded.IsActive,
                FirstTriggeredAt = excluded.FirstTriggeredAt,
                LastEvaluatedAt = excluded.LastEvaluatedAt,
                LastNotifiedAt = excluded.LastNotifiedAt,
                ClearedAt = excluded.ClearedAt,
                UpdatedAt = excluded.UpdatedAt;
            """;

        var now = ToTimestamp(_clock.GetTaipeiNow());
        await using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            foreach (var state in states)
            {
                await using var command = connection.CreateCommand();
                command.Transaction = transaction;
                command.CommandText = sql;
                command.Parameters.AddWithValue("$evaluationDate", ToDate(state.EvaluationDate));
                command.Parameters.AddWithValue("$baselineTradeDate", ToDate(state.BaselineTradeDate));
                command.Parameters.AddWithValue("$workbookPath", state.WorkbookPath);
                command.Parameters.AddWithValue("$sheetName", state.SheetName);
                command.Parameters.AddWithValue("$excelRow", state.ExcelRow);
                command.Parameters.AddWithValue("$stockCode", state.StockCode);
                command.Parameters.AddWithValue("$alertKind", (int)state.AlertKind);
                command.Parameters.AddWithValue("$maWindow", state.MaWindow);
                command.Parameters.AddWithValue("$isActive", state.IsActive ? 1 : 0);
                command.Parameters.AddWithValue("$firstTriggeredAt", ToTimestamp(state.FirstTriggeredAt));
                command.Parameters.AddWithValue("$lastEvaluatedAt", ToTimestamp(state.LastEvaluatedAt));
                command.Parameters.AddWithValue("$lastNotifiedAt", state.LastNotifiedAt is null ? DBNull.Value : ToTimestamp(state.LastNotifiedAt.Value));
                command.Parameters.AddWithValue("$clearedAt", state.ClearedAt is null ? DBNull.Value : ToTimestamp(state.ClearedAt.Value));
                command.Parameters.AddWithValue("$now", now);
                await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }

            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            await transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
            throw;
        }
    }

    public async Task SaveEvaluationRunAsync(IntradayEvaluationRunRecord run, CancellationToken cancellationToken)
    {
        const string sql = """
            INSERT INTO IntradayEvaluationRun
                (EvaluationDate, BaselineTradeDate, ScheduledAt, StartedAt, CompletedAt, Status,
                 HoldingCount, TriggeredCount, NewNotificationCount,
                 EntryAveragePriceInvalidCount, CurrentPriceInvalidCount, MissingMovingAverageCount,
                 SkippedReason, ErrorMessage, CreatedAt)
            VALUES
                ($evaluationDate, $baselineTradeDate, $scheduledAt, $startedAt, $completedAt, $status,
                 $holdingCount, $triggeredCount, $newNotificationCount,
                 $entryInvalidCount, $currentInvalidCount, $missingMaCount,
                 $skippedReason, $errorMessage, $now);
            """;

        await using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.Parameters.AddWithValue("$evaluationDate", ToDate(run.EvaluationDate));
        command.Parameters.AddWithValue("$baselineTradeDate", run.BaselineTradeDate is null ? DBNull.Value : ToDate(run.BaselineTradeDate.Value));
        command.Parameters.AddWithValue("$scheduledAt", ToTimestamp(run.ScheduledAt));
        command.Parameters.AddWithValue("$startedAt", run.StartedAt is null ? DBNull.Value : ToTimestamp(run.StartedAt.Value));
        command.Parameters.AddWithValue("$completedAt", run.CompletedAt is null ? DBNull.Value : ToTimestamp(run.CompletedAt.Value));
        command.Parameters.AddWithValue("$status", (int)run.Status);
        command.Parameters.AddWithValue("$holdingCount", run.HoldingCount);
        command.Parameters.AddWithValue("$triggeredCount", run.TriggeredCount);
        command.Parameters.AddWithValue("$newNotificationCount", run.NewNotificationCount);
        command.Parameters.AddWithValue("$entryInvalidCount", run.EntryAveragePriceInvalidCount);
        command.Parameters.AddWithValue("$currentInvalidCount", run.CurrentPriceInvalidCount);
        command.Parameters.AddWithValue("$missingMaCount", run.MissingMovingAverageCount);
        command.Parameters.AddWithValue("$skippedReason", (object?)run.SkippedReason ?? DBNull.Value);
        command.Parameters.AddWithValue("$errorMessage", (object?)run.ErrorMessage ?? DBNull.Value);
        command.Parameters.AddWithValue("$now", ToTimestamp(_clock.GetTaipeiNow()));
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<IntradayEvaluationRunRecord>> GetEvaluationRunsAsync(
        DateOnly evaluationDate,
        int limit,
        CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT Id, EvaluationDate, BaselineTradeDate, ScheduledAt, StartedAt, CompletedAt, Status,
                   HoldingCount, TriggeredCount, NewNotificationCount,
                   EntryAveragePriceInvalidCount, CurrentPriceInvalidCount, MissingMovingAverageCount,
                   SkippedReason, ErrorMessage
            FROM IntradayEvaluationRun
            WHERE EvaluationDate = $evaluationDate
            ORDER BY Id DESC
            LIMIT $limit;
            """;

        await using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.Parameters.AddWithValue("$evaluationDate", ToDate(evaluationDate));
        command.Parameters.AddWithValue("$limit", limit);

        var result = new List<IntradayEvaluationRunRecord>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            result.Add(new IntradayEvaluationRunRecord(
                reader.GetInt64(0),
                ParseDate(reader.GetString(1)),
                reader.IsDBNull(2) ? null : ParseDate(reader.GetString(2)),
                ParseTimestamp(reader.GetString(3)),
                reader.IsDBNull(4) ? null : ParseTimestamp(reader.GetString(4)),
                reader.IsDBNull(5) ? null : ParseTimestamp(reader.GetString(5)),
                (IntradayRunStatus)reader.GetInt32(6),
                reader.GetInt32(7),
                reader.GetInt32(8),
                reader.GetInt32(9),
                reader.GetInt32(10),
                reader.GetInt32(11),
                reader.GetInt32(12),
                reader.IsDBNull(13) ? null : reader.GetString(13),
                reader.IsDBNull(14) ? null : reader.GetString(14)));
        }

        return result;
    }

    private async Task<SqliteConnection> OpenConnectionAsync(CancellationToken cancellationToken)
    {
        var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await ExecuteNonQueryAsync(connection, "PRAGMA foreign_keys=ON; PRAGMA busy_timeout=10000;", cancellationToken).ConfigureAwait(false);
        return connection;
    }

    private static async Task ExecuteNonQueryAsync(SqliteConnection connection, string sql, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static string ToDate(DateOnly value) => value.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
    private static DateOnly ParseDate(string value) => DateOnly.ParseExact(value, "yyyy-MM-dd", CultureInfo.InvariantCulture);
    private static string ToTimestamp(DateTimeOffset value) => value.ToString("O", CultureInfo.InvariantCulture);
    private static DateTimeOffset ParseTimestamp(string value) => DateTimeOffset.Parse(value, CultureInfo.InvariantCulture);

    private const string SchemaSql = """
        -- 盤中通知去重狀態（2026-07-13 盤中／收盤流程拆分新增）。
        -- 同一條件持續成立時只通知一次；成立→不成立記錄 ClearedAt；再次成立可再次通知。
        -- 程式重啟後由本表恢復狀態，不得對仍持續成立的條件重複通知。
        CREATE TABLE IF NOT EXISTS IntradayAlertState (
            Id INTEGER NOT NULL PRIMARY KEY AUTOINCREMENT,   -- 流水號
            EvaluationDate TEXT NOT NULL,                    -- 盤中判斷日期（今天）
            BaselineTradeDate TEXT NOT NULL,                 -- 使用的上一交易日均價日期
            WorkbookPath TEXT NOT NULL,                      -- Excel 完整路徑
            SheetName TEXT NOT NULL,                         -- 客戶頁籤
            ExcelRow INTEGER NOT NULL,                       -- Excel 原始列號
            StockCode TEXT NOT NULL,                         -- 股票代碼
            AlertKind INTEGER NOT NULL,                      -- 1均線觸發／2缺技術資料／3現價無效／4進場價無效
            MaWindow INTEGER NOT NULL,                       -- 均線天數（5／20／120；非均線類通知為 0）
            IsActive INTEGER NOT NULL,                       -- 條件目前是否成立
            FirstTriggeredAt TEXT NOT NULL,                  -- 本輪首次成立時間
            LastEvaluatedAt TEXT NOT NULL,                   -- 最後一次判斷時間
            LastNotifiedAt TEXT NULL,                        -- 最後一次實際通知時間
            ClearedAt TEXT NULL,                             -- 條件由成立變不成立的時間
            CreatedAt TEXT NOT NULL,                         -- 建立時間
            UpdatedAt TEXT NOT NULL,                         -- 最後更新時間
            CONSTRAINT UQ_IntradayAlertState UNIQUE
                (EvaluationDate, WorkbookPath, SheetName, ExcelRow, StockCode, AlertKind, MaWindow)
        );
        CREATE INDEX IF NOT EXISTS IX_IntradayAlertState_Date_Workbook
            ON IntradayAlertState(EvaluationDate, WorkbookPath);

        -- 盤中每分鐘執行紀錄（2026-07-13 新增）。每分鐘只保存摘要，不保存整份持股快照；
        -- 與收盤更新的 JobRuns 完全分開，語意不得混用。
        CREATE TABLE IF NOT EXISTS IntradayEvaluationRun (
            Id INTEGER NOT NULL PRIMARY KEY AUTOINCREMENT,   -- 執行 ID
            EvaluationDate TEXT NOT NULL,                    -- 盤中判斷日期（今天）
            BaselineTradeDate TEXT NULL,                     -- 使用的上一交易日均價日期（基準未就緒時為 NULL）
            ScheduledAt TEXT NOT NULL,                       -- 本次 Tick 預定時間（對齊整分鐘；手動執行為觸發時間）
            StartedAt TEXT NULL,                             -- 實際開始時間（被略過時為 NULL）
            CompletedAt TEXT NULL,                           -- 完成時間
            Status INTEGER NOT NULL,                         -- 1成功／2部分成功／3失敗／4略過／5基準未就緒
            HoldingCount INTEGER NOT NULL DEFAULT 0,         -- 本次讀取持股數
            TriggeredCount INTEGER NOT NULL DEFAULT 0,       -- 目前成立條件（觸發通知）筆數
            NewNotificationCount INTEGER NOT NULL DEFAULT 0, -- 本次新通知筆數（去重後）
            EntryAveragePriceInvalidCount INTEGER NOT NULL DEFAULT 0, -- 進場價/平均價異常筆數
            CurrentPriceInvalidCount INTEGER NOT NULL DEFAULT 0,      -- 現價 DDE 異常筆數
            MissingMovingAverageCount INTEGER NOT NULL DEFAULT 0,     -- 缺基準均價筆數
            SkippedReason TEXT NULL,                         -- 略過原因（上一 Tick 未完成、收盤更新執行中等）
            ErrorMessage TEXT NULL,                          -- 錯誤訊息
            CreatedAt TEXT NOT NULL                          -- 建立時間
        );
        CREATE INDEX IF NOT EXISTS IX_IntradayEvaluationRun_Date
            ON IntradayEvaluationRun(EvaluationDate, Id);
        """;
}
