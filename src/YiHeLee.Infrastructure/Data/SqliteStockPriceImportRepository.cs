using System.Globalization;
using Microsoft.Data.Sqlite;
using YiHeLee.Application.Abstractions;
using YiHeLee.Domain;

namespace YiHeLee.Infrastructure.Data;

/// <summary>
/// StockPriceImportJob／StockPriceImportTask 的 SQLite 存取層。與 <see cref="SqliteMarketDataRepository"/>
/// 共用同一個資料庫檔案，但職責完全分開：本類別只負責使用者手動回補批次的「進度」記錄，
/// 不含官方來源 HTTP 存取，也不直接寫入 StockDailyPrice（那是 IMarketPriceService／IMarketDataRepository 的職責）。
/// </summary>
public sealed class SqliteStockPriceImportRepository : IStockPriceImportRepository
{
    // 工作明細終結狀態：Succeeded／Holiday／Failed／Cancelled，代表本工作已不再處理中。
    private const string TerminalTaskStatuses = "(4,5,6,7)";

    private readonly string _connectionString;
    private readonly IClock _clock;

    public SqliteStockPriceImportRepository(string databasePath, IClock clock)
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
        await using var command = connection.CreateCommand();
        command.CommandText = SchemaSql;
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<StockPriceImportJobCreationResult> CreateJobAsync(
        OfficialPriceJobType jobType,
        int requestedTradingDays,
        DateOnly? targetDate,
        string timeZoneId,
        IReadOnlyList<StockPriceImportTaskDescriptor> tasks,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        var nowText = ToTimestamp(now);
        await using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            const string insertJobSql = """
                INSERT INTO StockPriceImportJob
                    (JobType, RequestedTradingDays, TargetDate, TimeZoneId, TotalTasks, Status, CreatedAt, UpdatedAt)
                VALUES
                    ($jobType, $requestedTradingDays, $targetDate, $timeZoneId, $totalTasks, $status, $now, $now)
                RETURNING Id;
                """;
            long jobId;
            await using (var command = connection.CreateCommand())
            {
                command.Transaction = transaction;
                command.CommandText = insertJobSql;
                command.Parameters.AddWithValue("$jobType", (int)jobType);
                command.Parameters.AddWithValue("$requestedTradingDays", requestedTradingDays);
                command.Parameters.AddWithValue("$targetDate", targetDate is null ? DBNull.Value : ToDate(targetDate.Value));
                command.Parameters.AddWithValue("$timeZoneId", timeZoneId);
                command.Parameters.AddWithValue("$totalTasks", tasks.Count);
                command.Parameters.AddWithValue("$status", (int)StockPriceImportJobStatus.Queued);
                command.Parameters.AddWithValue("$now", nowText);
                var result = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
                jobId = Convert.ToInt64(result, CultureInfo.InvariantCulture);
            }

            var taskIds = new Dictionary<(MarketType, DateOnly), long>();
            const string insertTaskSql = """
                INSERT INTO StockPriceImportTask
                    (JobId, MarketType, RequestedDate, Status, CreatedAt, UpdatedAt)
                VALUES
                    ($jobId, $marketType, $requestedDate, $status, $now, $now)
                RETURNING Id;
                """;
            foreach (var task in tasks)
            {
                await using var command = connection.CreateCommand();
                command.Transaction = transaction;
                command.CommandText = insertTaskSql;
                command.Parameters.AddWithValue("$jobId", jobId);
                command.Parameters.AddWithValue("$marketType", (int)task.MarketType);
                command.Parameters.AddWithValue("$requestedDate", ToDate(task.RequestedDate));
                command.Parameters.AddWithValue("$status", (int)StockPriceImportTaskStatus.Queued);
                command.Parameters.AddWithValue("$now", nowText);
                var result = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
                taskIds[(task.MarketType, task.RequestedDate)] = Convert.ToInt64(result, CultureInfo.InvariantCulture);
            }

            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return new StockPriceImportJobCreationResult(jobId, taskIds);
        }
        catch
        {
            await transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
            throw;
        }
    }

    public async Task MarkJobRunningAsync(long jobId, DateTimeOffset startedAt, CancellationToken cancellationToken)
    {
        const string sql = """
            UPDATE StockPriceImportJob
            SET Status = $status, StartedAt = $startedAt, UpdatedAt = $now
            WHERE Id = $jobId;
            """;
        await using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.Parameters.AddWithValue("$status", (int)StockPriceImportJobStatus.Running);
        command.Parameters.AddWithValue("$startedAt", ToTimestamp(startedAt));
        command.Parameters.AddWithValue("$now", ToTimestamp(_clock.GetTaipeiNow()));
        command.Parameters.AddWithValue("$jobId", jobId);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task StartTaskAsync(long taskId, DateTimeOffset startedAt, CancellationToken cancellationToken)
    {
        const string sql = """
            UPDATE StockPriceImportTask
            SET Status = $status, StartedAt = $startedAt, UpdatedAt = $now
            WHERE Id = $taskId;
            """;
        await using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.Parameters.AddWithValue("$status", (int)StockPriceImportTaskStatus.Running);
        command.Parameters.AddWithValue("$startedAt", ToTimestamp(startedAt));
        command.Parameters.AddWithValue("$now", ToTimestamp(_clock.GetTaipeiNow()));
        command.Parameters.AddWithValue("$taskId", taskId);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task CompleteTaskAsync(
        long taskId,
        StockPriceImportTaskStatus status,
        DateOnly? actualTradeDate,
        string? sourceUrl,
        int retryCount,
        int totalRows,
        int insertedRows,
        int updatedRows,
        int skippedRows,
        int failedRows,
        DateTimeOffset completedAt,
        string? errorMessage,
        CancellationToken cancellationToken)
    {
        var processedRows = insertedRows + updatedRows + skippedRows + failedRows;
        const string sql = """
            UPDATE StockPriceImportTask
            SET Status = $status,
                ActualTradeDate = $actualTradeDate,
                SourceUrl = $sourceUrl,
                RetryCount = $retryCount,
                TotalRows = $totalRows,
                ProcessedRows = $processedRows,
                InsertedRows = $insertedRows,
                UpdatedRows = $updatedRows,
                SkippedRows = $skippedRows,
                FailedRows = $failedRows,
                ProgressPercent = 100,
                CompletedAt = $completedAt,
                ErrorMessage = $errorMessage,
                UpdatedAt = $now
            WHERE Id = $taskId;
            """;

        long jobId;
        await using (var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false))
        {
            await using (var command = connection.CreateCommand())
            {
                command.CommandText = sql;
                command.Parameters.AddWithValue("$status", (int)status);
                command.Parameters.AddWithValue("$actualTradeDate", actualTradeDate is null ? DBNull.Value : ToDate(actualTradeDate.Value));
                command.Parameters.AddWithValue("$sourceUrl", (object?)sourceUrl ?? DBNull.Value);
                command.Parameters.AddWithValue("$retryCount", retryCount);
                command.Parameters.AddWithValue("$totalRows", totalRows);
                command.Parameters.AddWithValue("$processedRows", processedRows);
                command.Parameters.AddWithValue("$insertedRows", insertedRows);
                command.Parameters.AddWithValue("$updatedRows", updatedRows);
                command.Parameters.AddWithValue("$skippedRows", skippedRows);
                command.Parameters.AddWithValue("$failedRows", failedRows);
                command.Parameters.AddWithValue("$completedAt", ToTimestamp(completedAt));
                command.Parameters.AddWithValue("$errorMessage", (object?)errorMessage ?? DBNull.Value);
                command.Parameters.AddWithValue("$now", ToTimestamp(_clock.GetTaipeiNow()));
                command.Parameters.AddWithValue("$taskId", taskId);
                await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }

            await using (var command = connection.CreateCommand())
            {
                command.CommandText = "SELECT JobId FROM StockPriceImportTask WHERE Id = $taskId;";
                command.Parameters.AddWithValue("$taskId", taskId);
                var result = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
                jobId = Convert.ToInt64(result, CultureInfo.InvariantCulture);
            }

            await RecalculateJobProgressAsync(connection, jobId, cancellationToken).ConfigureAwait(false);
        }
    }

    public async Task FinalizeJobAsync(long jobId, DateTimeOffset completedAt, CancellationToken cancellationToken)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await RecalculateJobProgressAsync(connection, jobId, cancellationToken).ConfigureAwait(false);

        const string countSql = """
            SELECT
                (SELECT COUNT(*) FROM StockPriceImportTask WHERE JobId = $jobId),
                (SELECT COUNT(*) FROM StockPriceImportTask WHERE JobId = $jobId AND Status = 6),
                (SELECT COUNT(*) FROM StockPriceImportTask WHERE JobId = $jobId AND Status = 7);
            """;
        int total, failed, cancelled;
        await using (var command = connection.CreateCommand())
        {
            command.CommandText = countSql;
            command.Parameters.AddWithValue("$jobId", jobId);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            await reader.ReadAsync(cancellationToken).ConfigureAwait(false);
            total = reader.GetInt32(0);
            failed = reader.GetInt32(1);
            cancelled = reader.GetInt32(2);
        }

        var finalStatus = cancelled > 0
            ? StockPriceImportJobStatus.Cancelled
            : failed == 0
                ? StockPriceImportJobStatus.Completed
                : failed == total
                    ? StockPriceImportJobStatus.Failed
                    : StockPriceImportJobStatus.CompletedWithErrors;

        const string sql = """
            UPDATE StockPriceImportJob
            SET Status = $status, CompletedAt = $completedAt, UpdatedAt = $now
            WHERE Id = $jobId;
            """;
        await using var updateCommand = connection.CreateCommand();
        updateCommand.CommandText = sql;
        updateCommand.Parameters.AddWithValue("$status", (int)finalStatus);
        updateCommand.Parameters.AddWithValue("$completedAt", ToTimestamp(completedAt));
        updateCommand.Parameters.AddWithValue("$now", ToTimestamp(_clock.GetTaipeiNow()));
        updateCommand.Parameters.AddWithValue("$jobId", jobId);
        await updateCommand.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task CancelJobAsync(long jobId, DateTimeOffset cancelledAt, CancellationToken cancellationToken)
    {
        const string cancelTasksSql = """
            UPDATE StockPriceImportTask
            SET Status = 7, CompletedAt = COALESCE(CompletedAt, $cancelledAt), ProgressPercent = 100,
                ErrorMessage = COALESCE(ErrorMessage, '使用者已取消本次回補。'), UpdatedAt = $now
            WHERE JobId = $jobId AND Status IN (1,2,3);
            """;
        await using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using (var command = connection.CreateCommand())
        {
            command.CommandText = cancelTasksSql;
            command.Parameters.AddWithValue("$cancelledAt", ToTimestamp(cancelledAt));
            command.Parameters.AddWithValue("$now", ToTimestamp(_clock.GetTaipeiNow()));
            command.Parameters.AddWithValue("$jobId", jobId);
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        await RecalculateJobProgressAsync(connection, jobId, cancellationToken).ConfigureAwait(false);

        const string sql = """
            UPDATE StockPriceImportJob
            SET Status = $status, CompletedAt = $completedAt, ErrorMessage = COALESCE(ErrorMessage, '使用者已取消本次回補。'), UpdatedAt = $now
            WHERE Id = $jobId;
            """;
        await using var updateCommand = connection.CreateCommand();
        updateCommand.CommandText = sql;
        updateCommand.Parameters.AddWithValue("$status", (int)StockPriceImportJobStatus.Cancelled);
        updateCommand.Parameters.AddWithValue("$completedAt", ToTimestamp(cancelledAt));
        updateCommand.Parameters.AddWithValue("$now", ToTimestamp(_clock.GetTaipeiNow()));
        updateCommand.Parameters.AddWithValue("$jobId", jobId);
        await updateCommand.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<StockPriceImportJobProgress?> GetJobProgressAsync(long jobId, CancellationToken cancellationToken)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = JobProgressSelectSql + " WHERE Id = $jobId;";
        command.Parameters.AddWithValue("$jobId", jobId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false) ? ReadJobProgress(reader) : null;
    }

    public async Task<StockPriceImportJobProgress?> GetLatestJobProgressAsync(CancellationToken cancellationToken)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = JobProgressSelectSql + " ORDER BY Id DESC LIMIT 1;";
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false) ? ReadJobProgress(reader) : null;
    }

    public async Task<IReadOnlyList<StockPriceImportTaskProgress>> GetTaskProgressAsync(long jobId, CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT Id, JobId, MarketType, RequestedDate, ActualTradeDate, SourceUrl, Status, RetryCount,
                   TotalRows, ProcessedRows, InsertedRows, UpdatedRows, SkippedRows, FailedRows, ProgressPercent,
                   StartedAt, CompletedAt, ErrorMessage
            FROM StockPriceImportTask
            WHERE JobId = $jobId
            ORDER BY MarketType, RequestedDate DESC;
            """;
        await using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.Parameters.AddWithValue("$jobId", jobId);

        var results = new List<StockPriceImportTaskProgress>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            results.Add(new StockPriceImportTaskProgress(
                reader.GetInt64(0),
                reader.GetInt64(1),
                (MarketType)reader.GetInt32(2),
                DateOnly.ParseExact(reader.GetString(3), "yyyy-MM-dd", CultureInfo.InvariantCulture),
                reader.IsDBNull(4) ? null : DateOnly.ParseExact(reader.GetString(4), "yyyy-MM-dd", CultureInfo.InvariantCulture),
                reader.IsDBNull(5) ? null : reader.GetString(5),
                (StockPriceImportTaskStatus)reader.GetInt32(6),
                reader.GetInt32(7),
                reader.GetInt32(8),
                reader.GetInt32(9),
                reader.GetInt32(10),
                reader.GetInt32(11),
                reader.GetInt32(12),
                reader.GetInt32(13),
                reader.GetDecimal(14),
                reader.IsDBNull(15) ? null : DateTimeOffset.Parse(reader.GetString(15), CultureInfo.InvariantCulture),
                reader.IsDBNull(16) ? null : DateTimeOffset.Parse(reader.GetString(16), CultureInfo.InvariantCulture),
                reader.IsDBNull(17) ? null : reader.GetString(17)));
        }

        return results;
    }

    /// <summary>
    /// 依 StockPriceImportTask 明細列彙總批次整體進度；不依賴記憶體計數器，確保多個並行工作完成時
    /// 彼此更新不會互相覆蓋，且畫面重新整理／程式重啟後仍可從資料庫取得正確進度。
    /// </summary>
    private static async Task RecalculateJobProgressAsync(SqliteConnection connection, long jobId, CancellationToken cancellationToken)
    {
        const string sql = $"""
            UPDATE StockPriceImportJob
            SET TotalTasks = (SELECT COUNT(*) FROM StockPriceImportTask WHERE JobId = $jobId),
                CompletedTasks = (SELECT COUNT(*) FROM StockPriceImportTask WHERE JobId = $jobId AND Status IN {TerminalTaskStatuses}),
                SuccessTasks = (SELECT COUNT(*) FROM StockPriceImportTask WHERE JobId = $jobId AND Status = 4),
                SkippedTasks = (SELECT COUNT(*) FROM StockPriceImportTask WHERE JobId = $jobId AND Status = 5),
                FailedTasks = (SELECT COUNT(*) FROM StockPriceImportTask WHERE JobId = $jobId AND Status = 6),
                TotalRows = (SELECT COALESCE(SUM(TotalRows), 0) FROM StockPriceImportTask WHERE JobId = $jobId),
                ProcessedRows = (SELECT COALESCE(SUM(ProcessedRows), 0) FROM StockPriceImportTask WHERE JobId = $jobId),
                InsertedRows = (SELECT COALESCE(SUM(InsertedRows), 0) FROM StockPriceImportTask WHERE JobId = $jobId),
                UpdatedRows = (SELECT COALESCE(SUM(UpdatedRows), 0) FROM StockPriceImportTask WHERE JobId = $jobId),
                SkippedRows = (SELECT COALESCE(SUM(SkippedRows), 0) FROM StockPriceImportTask WHERE JobId = $jobId),
                FailedRows = (SELECT COALESCE(SUM(FailedRows), 0) FROM StockPriceImportTask WHERE JobId = $jobId),
                ProgressPercent = CASE WHEN (SELECT COUNT(*) FROM StockPriceImportTask WHERE JobId = $jobId) = 0 THEN 0
                    ELSE 100.0 * (SELECT COUNT(*) FROM StockPriceImportTask WHERE JobId = $jobId AND Status IN {TerminalTaskStatuses})
                         / (SELECT COUNT(*) FROM StockPriceImportTask WHERE JobId = $jobId) END,
                Status = CASE WHEN Status IN (1) THEN 2 ELSE Status END,
                UpdatedAt = $now
            WHERE Id = $jobId;
            """;
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.Parameters.AddWithValue("$jobId", jobId);
        command.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture));
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static StockPriceImportJobProgress ReadJobProgress(SqliteDataReader reader) => new(
        reader.GetInt64(0),
        (OfficialPriceJobType)reader.GetInt32(1),
        reader.GetInt32(2),
        reader.IsDBNull(3) ? null : DateOnly.ParseExact(reader.GetString(3), "yyyy-MM-dd", CultureInfo.InvariantCulture),
        reader.GetString(4),
        reader.GetInt32(5),
        reader.GetInt32(6),
        reader.GetInt32(7),
        reader.GetInt32(8),
        reader.GetInt32(9),
        reader.GetInt32(10),
        reader.GetInt32(11),
        reader.GetInt32(12),
        reader.GetInt32(13),
        reader.GetInt32(14),
        reader.GetInt32(15),
        reader.GetDecimal(16),
        (StockPriceImportJobStatus)reader.GetInt32(17),
        reader.IsDBNull(18) ? null : DateTimeOffset.Parse(reader.GetString(18), CultureInfo.InvariantCulture),
        reader.IsDBNull(19) ? null : DateTimeOffset.Parse(reader.GetString(19), CultureInfo.InvariantCulture),
        reader.IsDBNull(20) ? null : reader.GetString(20));

    private const string JobProgressSelectSql = """
        SELECT Id, JobType, RequestedTradingDays, TargetDate, TimeZoneId, TotalTasks, CompletedTasks, SuccessTasks,
               FailedTasks, SkippedTasks, TotalRows, ProcessedRows, InsertedRows, UpdatedRows, SkippedRows, FailedRows,
               ProgressPercent, Status, StartedAt, CompletedAt, ErrorMessage
        FROM StockPriceImportJob
        """;

    private async Task<SqliteConnection> OpenConnectionAsync(CancellationToken cancellationToken)
    {
        var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var pragma = connection.CreateCommand();
        pragma.CommandText = "PRAGMA foreign_keys=ON; PRAGMA busy_timeout=10000;";
        await pragma.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        return connection;
    }

    private static string ToDate(DateOnly value) => value.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
    private static string ToTimestamp(DateTimeOffset value) => value.ToString("O", CultureInfo.InvariantCulture);

    private const string SchemaSql = """
        -- 使用者手動觸發的歷史收盤價回補批次：一次「立即回補」對應一筆。
        CREATE TABLE IF NOT EXISTS StockPriceImportJob (
            Id INTEGER NOT NULL PRIMARY KEY AUTOINCREMENT,       -- 批次編號
            JobType INTEGER NOT NULL,                             -- 工作類型（固定為2歷史回補）
            RequestedTradingDays INTEGER NOT NULL,                 -- 要求的有效交易日數
            TargetDate TEXT NULL,                                  -- 回溯起算的基準日（觸發當下台北日期）
            TimeZoneId TEXT NOT NULL,                              -- 時區，固定 Asia/Taipei
            TotalTasks INTEGER NOT NULL DEFAULT 0,                 -- 總工作數
            CompletedTasks INTEGER NOT NULL DEFAULT 0,             -- 已完成工作數（含成功／休市／失敗／取消）
            SuccessTasks INTEGER NOT NULL DEFAULT 0,               -- 成功工作數
            FailedTasks INTEGER NOT NULL DEFAULT 0,                -- 失敗工作數
            SkippedTasks INTEGER NOT NULL DEFAULT 0,               -- 略過工作數（休市等合法零筆）
            TotalRows INTEGER NOT NULL DEFAULT 0,                  -- 總資料筆數
            ProcessedRows INTEGER NOT NULL DEFAULT 0,              -- 已處理筆數
            InsertedRows INTEGER NOT NULL DEFAULT 0,               -- 新增筆數
            UpdatedRows INTEGER NOT NULL DEFAULT 0,                -- 更新筆數
            SkippedRows INTEGER NOT NULL DEFAULT 0,                -- 略過筆數
            FailedRows INTEGER NOT NULL DEFAULT 0,                 -- 失敗筆數
            ProgressPercent NUMERIC NOT NULL DEFAULT 0,            -- 整體進度百分比
            Status INTEGER NOT NULL,                               -- 執行狀態
            StartedAt TEXT NULL,                                   -- 開始時間
            CompletedAt TEXT NULL,                                 -- 完成時間
            ErrorMessage TEXT NULL,                                -- 錯誤訊息
            CreatedAt TEXT NOT NULL,
            UpdatedAt TEXT NOT NULL
        );
        CREATE INDEX IF NOT EXISTS IX_StockPriceImportJob_Status ON StockPriceImportJob(Status, CreatedAt);

        -- 一個抓取工作＝一個市場＋一個交易日期；每一列完整下載、解析、驗證後才會成批寫入 StockDailyPrice。
        CREATE TABLE IF NOT EXISTS StockPriceImportTask (
            Id INTEGER NOT NULL PRIMARY KEY AUTOINCREMENT,       -- 工作編號
            JobId INTEGER NOT NULL,                               -- 所屬批次編號
            MarketType INTEGER NOT NULL,                          -- 1上市(TWSE)／2上櫃(TPEx)
            RequestedDate TEXT NOT NULL,                          -- 請求日期
            ActualTradeDate TEXT NULL,                            -- 官方回報的實際交易日期（可能與請求日期不同）
            SourceUrl TEXT NULL,                                  -- 來源網址
            Status INTEGER NOT NULL,                              -- 工作狀態
            RetryCount INTEGER NOT NULL DEFAULT 0,                -- 重試次數
            TotalRows INTEGER NOT NULL DEFAULT 0,                 -- 總筆數
            ProcessedRows INTEGER NOT NULL DEFAULT 0,             -- 已處理筆數
            InsertedRows INTEGER NOT NULL DEFAULT 0,              -- 新增筆數
            UpdatedRows INTEGER NOT NULL DEFAULT 0,               -- 更新筆數
            SkippedRows INTEGER NOT NULL DEFAULT 0,               -- 略過筆數
            FailedRows INTEGER NOT NULL DEFAULT 0,                -- 失敗筆數
            ProgressPercent NUMERIC NOT NULL DEFAULT 0,           -- 工作進度百分比
            StartedAt TEXT NULL,                                  -- 開始時間
            CompletedAt TEXT NULL,                                -- 完成時間
            ErrorMessage TEXT NULL,                               -- 錯誤訊息
            CreatedAt TEXT NOT NULL,
            UpdatedAt TEXT NOT NULL,
            FOREIGN KEY (JobId) REFERENCES StockPriceImportJob(Id),
            CONSTRAINT UQ_StockPriceImportTask UNIQUE (JobId, MarketType, RequestedDate)
        );
        CREATE INDEX IF NOT EXISTS IX_StockPriceImportTask_Job_Status ON StockPriceImportTask(JobId, Status);
        """;
}
