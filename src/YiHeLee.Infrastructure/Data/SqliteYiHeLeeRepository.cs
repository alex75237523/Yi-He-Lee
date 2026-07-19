using System.Globalization;
using Microsoft.Data.Sqlite;
using YiHeLee.Application.Abstractions;
using YiHeLee.Domain;

namespace YiHeLee.Infrastructure.Data;

public sealed class SqliteYiHeLeeRepository : IYiHeLeeRepository
{
    private readonly string _connectionString;
    private readonly IClock _clock;

    public SqliteYiHeLeeRepository(string databasePath, IClock clock)
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
        await ExecuteNonQueryAsync(connection, null, "PRAGMA journal_mode=WAL;", cancellationToken).ConfigureAwait(false);
        await MigrateEntryAveragePriceToCurrentPriceAsync(connection, cancellationToken).ConfigureAwait(false);
        await ExecuteNonQueryAsync(connection, null, SchemaSql, cancellationToken).ConfigureAwait(false);
        await MigrateCustomerHoldingSnapshotsAddCurrentPriceIssueAsync(connection, cancellationToken).ConfigureAwait(false);
        await MigrateCustomerHoldingSnapshotsAddEntryAveragePriceAsync(connection, cancellationToken).ConfigureAwait(false);
        await MigrateStrategyAlertsAddEntryAveragePriceAndUniqueKeyAsync(connection, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// DDE 現價無效時必須保存錯誤原因，不得只留 CurrentPrice 為 NULL 而無法追查原因。既有資料庫在
    /// 新增本欄位前建立，CREATE TABLE IF NOT EXISTS 不會為既有資料表補上新欄位，因此以
    /// PRAGMA table_info 檢查後用 ALTER TABLE ADD COLUMN 安全新增，不刪除或覆蓋既有資料列。
    /// </summary>
    private static async Task MigrateCustomerHoldingSnapshotsAddCurrentPriceIssueAsync(SqliteConnection connection, CancellationToken cancellationToken)
    {
        if (await ColumnExistsAsync(connection, "CustomerHoldingSnapshots", "CurrentPriceIssue", cancellationToken).ConfigureAwait(false))
        {
            return;
        }

        await ExecuteNonQueryAsync(
            connection, null,
            "ALTER TABLE CustomerHoldingSnapshots ADD COLUMN CurrentPriceIssue TEXT NULL;",
            cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// 2026-07-11 需求恢復：客戶 Excel「進場價/平均價」與「現價」是兩個必須同時判斷的獨立欄位，
    /// 因此在 <see cref="CustomerHoldingSnapshots"/> 新增 EntryAveragePrice／EntryAveragePriceIssue 欄位，
    /// 以既有 PRAGMA table_info 檢查後 ALTER TABLE ADD COLUMN 安全新增，不刪除或覆蓋既有資料列。
    /// </summary>
    private static async Task MigrateCustomerHoldingSnapshotsAddEntryAveragePriceAsync(SqliteConnection connection, CancellationToken cancellationToken)
    {
        if (!await ColumnExistsAsync(connection, "CustomerHoldingSnapshots", "EntryAveragePrice", cancellationToken).ConfigureAwait(false))
        {
            await ExecuteNonQueryAsync(
                connection, null,
                "ALTER TABLE CustomerHoldingSnapshots ADD COLUMN EntryAveragePrice NUMERIC NULL;",
                cancellationToken).ConfigureAwait(false);
        }

        if (!await ColumnExistsAsync(connection, "CustomerHoldingSnapshots", "EntryAveragePriceIssue", cancellationToken).ConfigureAwait(false))
        {
            await ExecuteNonQueryAsync(
                connection, null,
                "ALTER TABLE CustomerHoldingSnapshots ADD COLUMN EntryAveragePriceIssue TEXT NULL;",
                cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// 2026-07-11 需求恢復：同一筆持股現在可能同時產生「進場價/平均價異常」與「現價異常」兩筆通知；
    /// 舊唯一鍵 (TradeDate, WorkbookPath, SheetName, ExcelRow, StockCode) 只以股票識別、不含 AlertKind，
    /// 會讓後寫入的通知覆蓋先寫入的通知。SQLite 無法直接修改既有 UNIQUE 約束，因此在唯一鍵新增
    /// AlertKind 的同時，採「改名舊表→由 SchemaSql 建新表→搬移→刪舊表」方式重建，並同時新增
    /// EntryAveragePrice／EntryAveragePriceIssue／CurrentPriceIssue 欄位；舊資料列原樣搬移，新欄位為 NULL。
    /// </summary>
    private static async Task MigrateStrategyAlertsAddEntryAveragePriceAndUniqueKeyAsync(SqliteConnection connection, CancellationToken cancellationToken)
    {
        if (await ColumnExistsAsync(connection, "StrategyAlerts", "EntryAveragePrice", cancellationToken).ConfigureAwait(false))
        {
            return;
        }

        const string copySql = """
            INSERT INTO StrategyAlerts
                (Id, JobId, TradeDate, AlertKind, WorkbookPath, SheetName, CustomerName, ExcelRow,
                 StockCode, StockName, CurrentPrice, Quantity, ClosePrice,
                 MovingAverage5, MovingAverage20, MovingAverage60, MovingAverage120,
                 TriggeredMa5, TriggeredMa20, TriggeredMa120, TriggerDescription,
                 MarketType, IndicatorType, SourceUrl, CreatedAt, UpdatedAt)
            SELECT Id, JobId, TradeDate, AlertKind, WorkbookPath, SheetName, CustomerName, ExcelRow,
                   StockCode, StockName, CurrentPrice, Quantity, ClosePrice,
                   MovingAverage5, MovingAverage20, MovingAverage60, MovingAverage120,
                   TriggeredMa5, TriggeredMa20, TriggeredMa120, TriggerDescription,
                   MarketType, IndicatorType, SourceUrl, CreatedAt, UpdatedAt
            FROM StrategyAlerts_Legacy;
            """;

        await ExecuteNonQueryAsync(connection, null, "ALTER TABLE StrategyAlerts RENAME TO StrategyAlerts_Legacy;", cancellationToken).ConfigureAwait(false);
        await ExecuteNonQueryAsync(connection, null, SchemaSql, cancellationToken).ConfigureAwait(false);
        await ExecuteNonQueryAsync(connection, null, copySql, cancellationToken).ConfigureAwait(false);
        await ExecuteNonQueryAsync(connection, null, "DROP TABLE StrategyAlerts_Legacy;", cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// 2026-07-11 需求更正：策略比較基準由「進場價／平均價」改為 Excel「現價」欄位（外部 DDE），
    /// 舊資料庫的 EntryAveragePrice 欄位改名為 CurrentPrice 並允許 NULL（現價無效的通知列沒有價格）。
    /// 舊列的值原樣保留（當時比較基準為進場價，僅屬歷史紀錄）。SQLite 無法直接卸除 NOT NULL，
    /// 因此以「改名舊表→由 SchemaSql 建新表→搬移→刪舊表」方式重建。
    /// 2026-07-11 之後另新增真正的 EntryAveragePrice 欄位（見 <see cref="MigrateCustomerHoldingSnapshotsAddEntryAveragePriceAsync"/>／
    /// <see cref="MigrateStrategyAlertsAddEntryAveragePriceAndUniqueKeyAsync"/>），欄位名稱恰好與本次歷史搬移
    /// 使用的舊欄位名稱相同；因此本方法額外檢查 CurrentPrice 是否已存在，只有「確實是尚未改名的舊格式
    /// （沒有 CurrentPrice）」才觸發搬移，避免把新版真正的 EntryAveragePrice 資料誤判為舊格式並覆蓋 CurrentPrice。
    /// </summary>
    private static async Task MigrateEntryAveragePriceToCurrentPriceAsync(SqliteConnection connection, CancellationToken cancellationToken)
    {
        var migrations = new (string Table, string CopySql)[]
        {
            ("CustomerHoldingSnapshots", """
                INSERT INTO CustomerHoldingSnapshots
                    (Id, JobId, SnapshotDate, WorkbookPath, SheetName, CustomerName, ExcelRow,
                     StockCode, StockName, CurrentPrice, Quantity, HoldingKey, CreatedAt)
                SELECT Id, JobId, SnapshotDate, WorkbookPath, SheetName, CustomerName, ExcelRow,
                       StockCode, StockName, EntryAveragePrice, Quantity, HoldingKey, CreatedAt
                FROM CustomerHoldingSnapshots_Legacy;
                """),
            ("StrategyAlerts", """
                INSERT INTO StrategyAlerts
                    (Id, JobId, TradeDate, AlertKind, WorkbookPath, SheetName, CustomerName, ExcelRow,
                     StockCode, StockName, CurrentPrice, Quantity, ClosePrice,
                     MovingAverage5, MovingAverage20, MovingAverage60, MovingAverage120,
                     TriggeredMa5, TriggeredMa20, TriggeredMa120, TriggerDescription,
                     MarketType, IndicatorType, SourceUrl, CreatedAt, UpdatedAt)
                SELECT Id, JobId, TradeDate, AlertKind, WorkbookPath, SheetName, CustomerName, ExcelRow,
                       StockCode, StockName, EntryAveragePrice, Quantity, ClosePrice,
                       MovingAverage5, MovingAverage20, MovingAverage60, MovingAverage120,
                       TriggeredMa5, TriggeredMa20, TriggeredMa120, TriggerDescription,
                       MarketType, IndicatorType, SourceUrl, CreatedAt, UpdatedAt
                FROM StrategyAlerts_Legacy;
                """)
        };

        foreach (var (table, copySql) in migrations)
        {
            var hasLegacyEntryAveragePrice = await ColumnExistsAsync(connection, table, "EntryAveragePrice", cancellationToken).ConfigureAwait(false);
            var alreadyMigratedToCurrentPrice = await ColumnExistsAsync(connection, table, "CurrentPrice", cancellationToken).ConfigureAwait(false);
            if (!hasLegacyEntryAveragePrice || alreadyMigratedToCurrentPrice)
            {
                continue;
            }

            await ExecuteNonQueryAsync(connection, null, $"ALTER TABLE {table} RENAME TO {table}_Legacy;", cancellationToken).ConfigureAwait(false);
            await ExecuteNonQueryAsync(connection, null, SchemaSql, cancellationToken).ConfigureAwait(false);
            await ExecuteNonQueryAsync(connection, null, copySql, cancellationToken).ConfigureAwait(false);
            await ExecuteNonQueryAsync(connection, null, $"DROP TABLE {table}_Legacy;", cancellationToken).ConfigureAwait(false);
        }
    }

    private static async Task<bool> ColumnExistsAsync(SqliteConnection connection, string table, string column, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM pragma_table_info($table) WHERE name = $column;";
        command.Parameters.AddWithValue("$table", table);
        command.Parameters.AddWithValue("$column", column);
        var count = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        return Convert.ToInt32(count, CultureInfo.InvariantCulture) > 0;
    }

    public async Task<Guid> BeginJobAsync(DateOnly targetDate, int attemptNumber, DateTimeOffset startedAt, CancellationToken cancellationToken)
    {
        var jobId = Guid.NewGuid();
        const string sql = """
            INSERT INTO JobRuns
                (JobId, TargetDate, TaipeiStartedAt, AttemptNumber, Status)
            VALUES
                ($jobId, $targetDate, $startedAt, $attemptNumber, $status);
            """;

        await using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.Parameters.AddWithValue("$jobId", jobId.ToString("D"));
        command.Parameters.AddWithValue("$targetDate", ToDate(targetDate));
        command.Parameters.AddWithValue("$startedAt", ToTimestamp(startedAt));
        command.Parameters.AddWithValue("$attemptNumber", attemptNumber);
        command.Parameters.AddWithValue("$status", (int)JobStatus.Running);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        return jobId;
    }

    public async Task RecordJobDetailAsync(Guid jobId, CrawlBatch batch, string status, string? errorMessage, CancellationToken cancellationToken)
    {
        const string sql = """
            INSERT INTO JobRunDetails
                (JobId, TargetDate, SourceKey, SourceUrl, IndicatorType, MarketType, PageDate,
                 FetchCount, StartedAt, CompletedAt, Status, ErrorMessage)
            VALUES
                ($jobId, $targetDate, $sourceKey, $sourceUrl, $indicatorType, $marketType, $pageDate,
                 $fetchCount, $startedAt, $completedAt, $status, $errorMessage)
            ON CONFLICT(JobId, SourceKey, MarketType) DO UPDATE SET
                PageDate = excluded.PageDate,
                FetchCount = excluded.FetchCount,
                StartedAt = excluded.StartedAt,
                CompletedAt = excluded.CompletedAt,
                Status = excluded.Status,
                ErrorMessage = excluded.ErrorMessage;
            """;

        await using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.Parameters.AddWithValue("$jobId", jobId.ToString("D"));
        command.Parameters.AddWithValue("$targetDate", ToDate(batch.TargetDate));
        command.Parameters.AddWithValue("$sourceKey", batch.Source.SourceKey);
        command.Parameters.AddWithValue("$sourceUrl", batch.Source.Url.ToString());
        command.Parameters.AddWithValue("$indicatorType", (int)batch.Source.IndicatorType);
        command.Parameters.AddWithValue("$marketType", (int)batch.MarketType);
        command.Parameters.AddWithValue("$pageDate", ToDate(batch.PageDate));
        command.Parameters.AddWithValue("$fetchCount", batch.Items.Count);
        command.Parameters.AddWithValue("$startedAt", ToTimestamp(batch.StartedAt));
        command.Parameters.AddWithValue("$completedAt", ToTimestamp(batch.CompletedAt));
        command.Parameters.AddWithValue("$status", status);
        command.Parameters.AddWithValue("$errorMessage", (object?)errorMessage ?? DBNull.Value);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task RecordJobDetailFailureAsync(
        Guid jobId,
        SourceDefinition source,
        MarketType marketType,
        DateOnly targetDate,
        string status,
        string errorMessage,
        DateTimeOffset startedAt,
        DateTimeOffset completedAt,
        CancellationToken cancellationToken)
    {
        const string sql = """
            INSERT INTO JobRunDetails
                (JobId, TargetDate, SourceKey, SourceUrl, IndicatorType, MarketType, PageDate,
                 FetchCount, StartedAt, CompletedAt, Status, ErrorMessage)
            VALUES
                ($jobId, $targetDate, $sourceKey, $sourceUrl, $indicatorType, $marketType, NULL,
                 0, $startedAt, $completedAt, $status, $errorMessage)
            ON CONFLICT(JobId, SourceKey, MarketType) DO UPDATE SET
                FetchCount = 0,
                StartedAt = excluded.StartedAt,
                CompletedAt = excluded.CompletedAt,
                Status = excluded.Status,
                ErrorMessage = excluded.ErrorMessage;
            """;

        await using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.Parameters.AddWithValue("$jobId", jobId.ToString("D"));
        command.Parameters.AddWithValue("$targetDate", ToDate(targetDate));
        command.Parameters.AddWithValue("$sourceKey", source.SourceKey);
        command.Parameters.AddWithValue("$sourceUrl", source.Url.ToString());
        command.Parameters.AddWithValue("$indicatorType", (int)source.IndicatorType);
        command.Parameters.AddWithValue("$marketType", (int)marketType);
        command.Parameters.AddWithValue("$startedAt", ToTimestamp(startedAt));
        command.Parameters.AddWithValue("$completedAt", ToTimestamp(completedAt));
        command.Parameters.AddWithValue("$status", status);
        command.Parameters.AddWithValue("$errorMessage", errorMessage);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task SaveCompleteTechnicalBatchAsync(Guid jobId, IReadOnlyList<CrawlBatch> batches, CancellationToken cancellationToken)
    {
        if (batches.Count == 0)
        {
            throw new InvalidOperationException("沒有任何已驗證批次可寫入。");
        }

        var targetDates = batches.Select(x => x.TargetDate).Distinct().ToArray();
        if (targetDates.Length != 1 || batches.Any(x => x.PageDate != targetDates[0]))
        {
            throw new InvalidOperationException("批次日期不一致，禁止寫入正式資料表。");
        }

        await using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            foreach (var batch in batches)
            {
                // 每一批是完整清單；重跑前先刪除相同日期／類型／市場舊資料，再寫入本次完整結果。
                const string deleteSql = "DELETE FROM TechnicalIndicatorDaily WHERE TradeDate = $tradeDate AND IndicatorType = $indicatorType AND MarketType = $marketType;";
                await ExecuteParameterizedNonQueryAsync(
                    connection,
                    transaction,
                    deleteSql,
                    command =>
                    {
                        command.Parameters.AddWithValue("$tradeDate", ToDate(batch.TargetDate));
                        command.Parameters.AddWithValue("$indicatorType", (int)batch.Source.IndicatorType);
                        command.Parameters.AddWithValue("$marketType", (int)batch.MarketType);
                    },
                    cancellationToken).ConfigureAwait(false);

                foreach (var item in batch.Items)
                {
                    await UpsertStockAsync(connection, transaction, item, cancellationToken).ConfigureAwait(false);
                    await UpsertTechnicalIndicatorAsync(connection, transaction, item, cancellationToken).ConfigureAwait(false);
                }
            }

            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            await transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
            throw;
        }
    }

    public async Task<IReadOnlyList<TechnicalIndicator>> GetTechnicalIndicatorsAsync(DateOnly tradeDate, CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT t.TradeDate, t.IndicatorType, t.MarketType, t.StockCode, s.StockName,
                   t.ClosePrice, t.MovingAverage5, t.MovingAverage20, t.MovingAverage60, t.MovingAverage120,
                   t.SourceUrl, t.FetchStartedAt, t.FetchCompletedAt
            FROM TechnicalIndicatorDaily t
            INNER JOIN Stocks s ON s.StockCode = t.StockCode
            WHERE t.TradeDate = $tradeDate
            ORDER BY t.IndicatorType, t.MarketType, t.StockCode;
            """;

        var result = new List<TechnicalIndicator>();
        await using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.Parameters.AddWithValue("$tradeDate", ToDate(tradeDate));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            result.Add(new TechnicalIndicator(
                DateOnly.ParseExact(reader.GetString(0), "yyyy-MM-dd", CultureInfo.InvariantCulture),
                (IndicatorType)reader.GetInt32(1),
                (MarketType)reader.GetInt32(2),
                reader.GetString(3),
                reader.GetString(4),
                reader.GetDecimal(5),
                reader.GetDecimal(6),
                reader.GetDecimal(7),
                reader.GetDecimal(8),
                reader.GetDecimal(9),
                reader.GetString(10),
                DateTimeOffset.Parse(reader.GetString(11), CultureInfo.InvariantCulture),
                DateTimeOffset.Parse(reader.GetString(12), CultureInfo.InvariantCulture)));
        }

        return result;
    }

    public async Task SaveHoldingsAndAlertsAsync(
        Guid jobId,
        DateOnly tradeDate,
        string workbookPath,
        IReadOnlyList<CustomerHolding> holdings,
        IReadOnlyList<StrategyAlert> alerts,
        CancellationToken cancellationToken)
    {
        var normalizedWorkbookPath = Path.GetFullPath(workbookPath);
        await using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            // 同一天重跑時先清除該活頁簿舊快照，避免已刪除或已不再觸發的資料殘留。
            const string deleteHoldingsSql = "DELETE FROM CustomerHoldingSnapshots WHERE SnapshotDate = $tradeDate AND WorkbookPath = $workbookPath;";
            const string deleteAlertsSql = "DELETE FROM StrategyAlerts WHERE TradeDate = $tradeDate AND WorkbookPath = $workbookPath;";
            await ExecuteParameterizedNonQueryAsync(
                connection,
                transaction,
                deleteHoldingsSql,
                command =>
                {
                    command.Parameters.AddWithValue("$tradeDate", ToDate(tradeDate));
                    command.Parameters.AddWithValue("$workbookPath", normalizedWorkbookPath);
                },
                cancellationToken).ConfigureAwait(false);
            await ExecuteParameterizedNonQueryAsync(
                connection,
                transaction,
                deleteAlertsSql,
                command =>
                {
                    command.Parameters.AddWithValue("$tradeDate", ToDate(tradeDate));
                    command.Parameters.AddWithValue("$workbookPath", normalizedWorkbookPath);
                },
                cancellationToken).ConfigureAwait(false);

            foreach (var holding in holdings)
            {
                await UpsertHoldingAsync(connection, transaction, jobId, holding, cancellationToken).ConfigureAwait(false);
            }

            foreach (var alert in alerts)
            {
                await UpsertAlertAsync(connection, transaction, jobId, alert, cancellationToken).ConfigureAwait(false);
            }

            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            await transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
            throw;
        }
    }

    public async Task CompleteJobAsync(Guid jobId, JobRunSummary summary, CancellationToken cancellationToken)
    {
        const string sql = """
            UPDATE JobRuns
            SET TaipeiCompletedAt = $completedAt,
                Status = $status,
                Outcome = $outcome,
                Message = $message,
                CrawledCount = $crawledCount,
                HoldingCount = $holdingCount,
                AlertCount = $alertCount,
                MissingIndicatorCount = $missingCount
            WHERE JobId = $jobId;
            """;

        await using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.Parameters.AddWithValue("$completedAt", ToTimestamp(summary.CompletedAt));
        command.Parameters.AddWithValue("$status", (int)summary.Status);
        command.Parameters.AddWithValue("$outcome", (int)summary.Outcome);
        command.Parameters.AddWithValue("$message", summary.Message);
        command.Parameters.AddWithValue("$crawledCount", summary.CrawledCount);
        command.Parameters.AddWithValue("$holdingCount", summary.HoldingCount);
        command.Parameters.AddWithValue("$alertCount", summary.AlertCount);
        command.Parameters.AddWithValue("$missingCount", summary.MissingIndicatorCount);
        command.Parameters.AddWithValue("$jobId", jobId.ToString("D"));
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<int> GetAttemptCountAsync(DateOnly targetDate, CancellationToken cancellationToken)
    {
        const string sql = "SELECT COUNT(*) FROM JobRuns WHERE TargetDate = $targetDate;";
        await using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.Parameters.AddWithValue("$targetDate", ToDate(targetDate));
        return Convert.ToInt32(await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false), CultureInfo.InvariantCulture);
    }

    public Task<JobRunSummary?> GetLatestJobSummaryAsync(CancellationToken cancellationToken)
        => GetLatestJobSummaryCoreAsync(targetDate: null, cancellationToken);

    public Task<JobRunSummary?> GetLatestJobSummaryForDateAsync(DateOnly targetDate, CancellationToken cancellationToken)
        => GetLatestJobSummaryCoreAsync(targetDate, cancellationToken);

    private async Task<JobRunSummary?> GetLatestJobSummaryCoreAsync(DateOnly? targetDate, CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT JobId, TargetDate, Status, Outcome, COALESCE(Message, ''), AttemptNumber,
                   CrawledCount, HoldingCount, AlertCount, MissingIndicatorCount,
                   TaipeiStartedAt, COALESCE(TaipeiCompletedAt, TaipeiStartedAt)
            FROM JobRuns
            WHERE ($targetDate IS NULL OR TargetDate = $targetDate)
            ORDER BY TaipeiStartedAt DESC
            LIMIT 1;
            """;

        await using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.Parameters.AddWithValue("$targetDate", targetDate is null ? DBNull.Value : ToDate(targetDate.Value));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            return null;
        }

        return new JobRunSummary(
            Guid.Parse(reader.GetString(0)),
            DateOnly.ParseExact(reader.GetString(1), "yyyy-MM-dd", CultureInfo.InvariantCulture),
            (JobStatus)reader.GetInt32(2),
            reader.IsDBNull(3) ? RunOutcome.RetryableFailure : (RunOutcome)reader.GetInt32(3),
            reader.GetString(4),
            reader.GetInt32(5),
            reader.GetInt32(6),
            reader.GetInt32(7),
            reader.GetInt32(8),
            reader.GetInt32(9),
            DateTimeOffset.Parse(reader.GetString(10), CultureInfo.InvariantCulture),
            DateTimeOffset.Parse(reader.GetString(11), CultureInfo.InvariantCulture),
            []);
    }

    private async Task<SqliteConnection> OpenConnectionAsync(CancellationToken cancellationToken)
    {
        var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await ExecuteNonQueryAsync(connection, null, "PRAGMA foreign_keys=ON; PRAGMA busy_timeout=10000;", cancellationToken).ConfigureAwait(false);
        return connection;
    }

    private async Task UpsertStockAsync(SqliteConnection connection, SqliteTransaction transaction, TechnicalIndicator item, CancellationToken cancellationToken)
    {
        const string sql = """
            INSERT INTO Stocks (StockCode, StockName, CreatedAt, UpdatedAt)
            VALUES ($stockCode, $stockName, $now, $now)
            ON CONFLICT(StockCode) DO UPDATE SET
                StockName = excluded.StockName,
                UpdatedAt = excluded.UpdatedAt;
            """;
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        command.Parameters.AddWithValue("$stockCode", item.StockCode);
        command.Parameters.AddWithValue("$stockName", item.StockName);
        command.Parameters.AddWithValue("$now", ToTimestamp(_clock.GetTaipeiNow()));
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task UpsertTechnicalIndicatorAsync(SqliteConnection connection, SqliteTransaction transaction, TechnicalIndicator item, CancellationToken cancellationToken)
    {
        const string sql = """
            INSERT INTO TechnicalIndicatorDaily
                (TradeDate, IndicatorType, MarketType, StockCode, ClosePrice,
                 MovingAverage5, MovingAverage20, MovingAverage60, MovingAverage120,
                 SourceUrl, FetchStartedAt, FetchCompletedAt, CreatedAt, UpdatedAt)
            VALUES
                ($tradeDate, $indicatorType, $marketType, $stockCode, $closePrice,
                 $ma5, $ma20, $ma60, $ma120,
                 $sourceUrl, $fetchStartedAt, $fetchCompletedAt, $now, $now)
            ON CONFLICT(TradeDate, IndicatorType, MarketType, StockCode) DO UPDATE SET
                ClosePrice = excluded.ClosePrice,
                MovingAverage5 = excluded.MovingAverage5,
                MovingAverage20 = excluded.MovingAverage20,
                MovingAverage60 = excluded.MovingAverage60,
                MovingAverage120 = excluded.MovingAverage120,
                SourceUrl = excluded.SourceUrl,
                FetchStartedAt = excluded.FetchStartedAt,
                FetchCompletedAt = excluded.FetchCompletedAt,
                UpdatedAt = excluded.UpdatedAt;
            """;
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        command.Parameters.AddWithValue("$tradeDate", ToDate(item.TradeDate));
        command.Parameters.AddWithValue("$indicatorType", (int)item.IndicatorType);
        command.Parameters.AddWithValue("$marketType", (int)item.MarketType);
        command.Parameters.AddWithValue("$stockCode", item.StockCode);
        command.Parameters.AddWithValue("$closePrice", item.ClosePrice);
        command.Parameters.AddWithValue("$ma5", item.MovingAverage5);
        command.Parameters.AddWithValue("$ma20", item.MovingAverage20);
        command.Parameters.AddWithValue("$ma60", item.MovingAverage60);
        command.Parameters.AddWithValue("$ma120", item.MovingAverage120);
        command.Parameters.AddWithValue("$sourceUrl", item.SourceUrl);
        command.Parameters.AddWithValue("$fetchStartedAt", ToTimestamp(item.FetchStartedAt));
        command.Parameters.AddWithValue("$fetchCompletedAt", ToTimestamp(item.FetchCompletedAt));
        command.Parameters.AddWithValue("$now", ToTimestamp(_clock.GetTaipeiNow()));
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task UpsertHoldingAsync(SqliteConnection connection, SqliteTransaction transaction, Guid jobId, CustomerHolding holding, CancellationToken cancellationToken)
    {
        const string sql = """
            INSERT INTO CustomerHoldingSnapshots
                (JobId, SnapshotDate, WorkbookPath, SheetName, CustomerName, ExcelRow,
                 StockCode, StockName, CurrentPrice, CurrentPriceIssue,
                 EntryAveragePrice, EntryAveragePriceIssue, Quantity, HoldingKey, CreatedAt)
            VALUES
                ($jobId, $snapshotDate, $workbookPath, $sheetName, $customerName, $excelRow,
                 $stockCode, $stockName, $currentPrice, $currentPriceIssue,
                 $entryAveragePrice, $entryAveragePriceIssue, $quantity, $holdingKey, $createdAt)
            ON CONFLICT(SnapshotDate, HoldingKey) DO UPDATE SET
                JobId = excluded.JobId,
                CustomerName = excluded.CustomerName,
                StockName = excluded.StockName,
                CurrentPrice = excluded.CurrentPrice,
                CurrentPriceIssue = excluded.CurrentPriceIssue,
                EntryAveragePrice = excluded.EntryAveragePrice,
                EntryAveragePriceIssue = excluded.EntryAveragePriceIssue,
                Quantity = excluded.Quantity,
                CreatedAt = excluded.CreatedAt;
            """;
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        command.Parameters.AddWithValue("$jobId", jobId.ToString("D"));
        command.Parameters.AddWithValue("$snapshotDate", ToDate(holding.SnapshotDate));
        command.Parameters.AddWithValue("$workbookPath", holding.WorkbookPath);
        command.Parameters.AddWithValue("$sheetName", holding.SheetName);
        command.Parameters.AddWithValue("$customerName", holding.CustomerName);
        command.Parameters.AddWithValue("$excelRow", holding.ExcelRow);
        command.Parameters.AddWithValue("$stockCode", holding.StockCode);
        command.Parameters.AddWithValue("$stockName", holding.StockName);
        command.Parameters.AddWithValue("$currentPrice", holding.CurrentPrice is null ? DBNull.Value : holding.CurrentPrice.Value);
        command.Parameters.AddWithValue("$currentPriceIssue", (object?)holding.CurrentPriceIssue ?? DBNull.Value);
        command.Parameters.AddWithValue("$entryAveragePrice", holding.EntryAveragePrice is null ? DBNull.Value : holding.EntryAveragePrice.Value);
        command.Parameters.AddWithValue("$entryAveragePriceIssue", (object?)holding.EntryAveragePriceIssue ?? DBNull.Value);
        command.Parameters.AddWithValue("$quantity", holding.Quantity is null ? DBNull.Value : holding.Quantity.Value);
        command.Parameters.AddWithValue("$holdingKey", holding.HoldingKey);
        command.Parameters.AddWithValue("$createdAt", ToTimestamp(_clock.GetTaipeiNow()));
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task UpsertAlertAsync(SqliteConnection connection, SqliteTransaction transaction, Guid jobId, StrategyAlert alert, CancellationToken cancellationToken)
    {
        const string sql = """
            INSERT INTO StrategyAlerts
                (JobId, TradeDate, AlertKind, WorkbookPath, SheetName, CustomerName, ExcelRow,
                 StockCode, StockName, CurrentPrice, CurrentPriceIssue,
                 EntryAveragePrice, EntryAveragePriceIssue, Quantity, ClosePrice,
                 MovingAverage5, MovingAverage20, MovingAverage60, MovingAverage120,
                 TriggeredMa5, TriggeredMa20, TriggeredMa120, TriggerDescription,
                 MarketType, IndicatorType, SourceUrl, CreatedAt, UpdatedAt)
            VALUES
                ($jobId, $tradeDate, $alertKind, $workbookPath, $sheetName, $customerName, $excelRow,
                 $stockCode, $stockName, $currentPrice, $currentPriceIssue,
                 $entryAveragePrice, $entryAveragePriceIssue, $quantity, $closePrice,
                 $ma5, $ma20, $ma60, $ma120,
                 $triggeredMa5, $triggeredMa20, $triggeredMa120, $description,
                 $marketType, $indicatorType, $sourceUrl, $now, $now)
            ON CONFLICT(TradeDate, WorkbookPath, SheetName, ExcelRow, StockCode, AlertKind) DO UPDATE SET
                JobId = excluded.JobId,
                CustomerName = excluded.CustomerName,
                StockName = excluded.StockName,
                CurrentPrice = excluded.CurrentPrice,
                CurrentPriceIssue = excluded.CurrentPriceIssue,
                EntryAveragePrice = excluded.EntryAveragePrice,
                EntryAveragePriceIssue = excluded.EntryAveragePriceIssue,
                Quantity = excluded.Quantity,
                ClosePrice = excluded.ClosePrice,
                MovingAverage5 = excluded.MovingAverage5,
                MovingAverage20 = excluded.MovingAverage20,
                MovingAverage60 = excluded.MovingAverage60,
                MovingAverage120 = excluded.MovingAverage120,
                TriggeredMa5 = excluded.TriggeredMa5,
                TriggeredMa20 = excluded.TriggeredMa20,
                TriggeredMa120 = excluded.TriggeredMa120,
                TriggerDescription = excluded.TriggerDescription,
                MarketType = excluded.MarketType,
                IndicatorType = excluded.IndicatorType,
                SourceUrl = excluded.SourceUrl,
                UpdatedAt = excluded.UpdatedAt;
            """;
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        command.Parameters.AddWithValue("$jobId", jobId.ToString("D"));
        command.Parameters.AddWithValue("$tradeDate", ToDate(alert.TradeDate));
        command.Parameters.AddWithValue("$alertKind", (int)alert.AlertKind);
        command.Parameters.AddWithValue("$workbookPath", alert.WorkbookPath);
        command.Parameters.AddWithValue("$sheetName", alert.SheetName);
        command.Parameters.AddWithValue("$customerName", alert.CustomerName);
        command.Parameters.AddWithValue("$excelRow", alert.ExcelRow);
        command.Parameters.AddWithValue("$stockCode", alert.StockCode);
        command.Parameters.AddWithValue("$stockName", alert.StockName);
        command.Parameters.AddWithValue("$currentPrice", alert.CurrentPrice is null ? DBNull.Value : alert.CurrentPrice.Value);
        command.Parameters.AddWithValue("$currentPriceIssue", (object?)alert.CurrentPriceIssue ?? DBNull.Value);
        command.Parameters.AddWithValue("$entryAveragePrice", alert.EntryAveragePrice is null ? DBNull.Value : alert.EntryAveragePrice.Value);
        command.Parameters.AddWithValue("$entryAveragePriceIssue", (object?)alert.EntryAveragePriceIssue ?? DBNull.Value);
        command.Parameters.AddWithValue("$quantity", alert.Quantity is null ? DBNull.Value : alert.Quantity.Value);
        command.Parameters.AddWithValue("$closePrice", alert.ClosePrice is null ? DBNull.Value : alert.ClosePrice.Value);
        command.Parameters.AddWithValue("$ma5", alert.MovingAverage5 is null ? DBNull.Value : alert.MovingAverage5.Value);
        command.Parameters.AddWithValue("$ma20", alert.MovingAverage20 is null ? DBNull.Value : alert.MovingAverage20.Value);
        command.Parameters.AddWithValue("$ma60", alert.MovingAverage60 is null ? DBNull.Value : alert.MovingAverage60.Value);
        command.Parameters.AddWithValue("$ma120", alert.MovingAverage120 is null ? DBNull.Value : alert.MovingAverage120.Value);
        command.Parameters.AddWithValue("$triggeredMa5", alert.TriggeredMa5 ? 1 : 0);
        command.Parameters.AddWithValue("$triggeredMa20", alert.TriggeredMa20 ? 1 : 0);
        command.Parameters.AddWithValue("$triggeredMa120", alert.TriggeredMa120 ? 1 : 0);
        command.Parameters.AddWithValue("$description", alert.TriggerDescription);
        command.Parameters.AddWithValue("$marketType", alert.MarketType is null ? DBNull.Value : (int)alert.MarketType.Value);
        command.Parameters.AddWithValue("$indicatorType", alert.IndicatorType is null ? DBNull.Value : (int)alert.IndicatorType.Value);
        command.Parameters.AddWithValue("$sourceUrl", (object?)alert.SourceUrl ?? DBNull.Value);
        command.Parameters.AddWithValue("$now", ToTimestamp(_clock.GetTaipeiNow()));
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task ExecuteParameterizedNonQueryAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string sql,
        Action<SqliteCommand> configure,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        configure(command);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task ExecuteNonQueryAsync(SqliteConnection connection, SqliteTransaction? transaction, string sql, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static string ToDate(DateOnly value) => value.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
    private static string ToTimestamp(DateTimeOffset value) => value.ToString("O", CultureInfo.InvariantCulture);

    private const string SchemaSql = """
        CREATE TABLE IF NOT EXISTS Stocks (
            StockCode TEXT NOT NULL PRIMARY KEY,
            StockName TEXT NOT NULL,
            CreatedAt TEXT NOT NULL,
            UpdatedAt TEXT NOT NULL
        );
        CREATE TABLE IF NOT EXISTS TechnicalIndicatorDaily (
            Id INTEGER NOT NULL PRIMARY KEY AUTOINCREMENT,
            TradeDate TEXT NOT NULL,
            IndicatorType INTEGER NOT NULL,
            MarketType INTEGER NOT NULL,
            StockCode TEXT NOT NULL,
            ClosePrice NUMERIC NOT NULL,
            MovingAverage5 NUMERIC NOT NULL,
            MovingAverage20 NUMERIC NOT NULL,
            MovingAverage60 NUMERIC NOT NULL,
            MovingAverage120 NUMERIC NOT NULL,
            SourceUrl TEXT NOT NULL,
            FetchStartedAt TEXT NOT NULL,
            FetchCompletedAt TEXT NOT NULL,
            CreatedAt TEXT NOT NULL,
            UpdatedAt TEXT NOT NULL,
            FOREIGN KEY (StockCode) REFERENCES Stocks(StockCode),
            CONSTRAINT UQ_TechnicalIndicatorDaily UNIQUE (TradeDate, IndicatorType, MarketType, StockCode)
        );
        CREATE TABLE IF NOT EXISTS JobRuns (
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
        CREATE INDEX IF NOT EXISTS IX_JobRuns_TargetDate ON JobRuns(TargetDate, AttemptNumber);
        CREATE TABLE IF NOT EXISTS JobRunDetails (
            Id INTEGER NOT NULL PRIMARY KEY AUTOINCREMENT,
            JobId TEXT NOT NULL,
            TargetDate TEXT NOT NULL,
            SourceKey TEXT NOT NULL,
            SourceUrl TEXT NOT NULL,
            IndicatorType INTEGER NOT NULL,
            MarketType INTEGER NOT NULL,
            PageDate TEXT NULL,
            FetchCount INTEGER NOT NULL DEFAULT 0,
            StartedAt TEXT NOT NULL,
            CompletedAt TEXT NOT NULL,
            Status TEXT NOT NULL,
            ErrorMessage TEXT NULL,
            FOREIGN KEY (JobId) REFERENCES JobRuns(JobId),
            CONSTRAINT UQ_JobRunDetails UNIQUE (JobId, SourceKey, MarketType)
        );
        CREATE TABLE IF NOT EXISTS CustomerHoldingSnapshots (
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
            CurrentPriceIssue TEXT NULL,
            EntryAveragePrice NUMERIC NULL,
            EntryAveragePriceIssue TEXT NULL,
            Quantity NUMERIC NULL,
            HoldingKey TEXT NOT NULL,
            CreatedAt TEXT NOT NULL,
            FOREIGN KEY (JobId) REFERENCES JobRuns(JobId),
            CONSTRAINT UQ_CustomerHoldingSnapshots UNIQUE (SnapshotDate, HoldingKey)
        );
        CREATE TABLE IF NOT EXISTS StrategyAlerts (
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
            CurrentPriceIssue TEXT NULL,
            EntryAveragePrice NUMERIC NULL,
            EntryAveragePriceIssue TEXT NULL,
            Quantity NUMERIC NULL,
            ClosePrice NUMERIC NULL,
            MovingAverage5 NUMERIC NULL,
            MovingAverage20 NUMERIC NULL,
            MovingAverage60 NUMERIC NULL,
            MovingAverage120 NUMERIC NULL,
            TriggeredMa5 INTEGER NOT NULL,   -- 2026-07-19 新語意：子條件「現價 < MA5」是否成立
            TriggeredMa20 INTEGER NOT NULL,  -- 2026-07-19 新語意：子條件「進場價/平均價 > MA20」是否成立
            TriggeredMa120 INTEGER NOT NULL, -- 固定為 0（MA120 不再參與策略）；整體觸發＝TriggeredMa5 AND TriggeredMa20
            TriggerDescription TEXT NOT NULL,
            MarketType INTEGER NULL,
            IndicatorType INTEGER NULL,
            SourceUrl TEXT NULL,
            CreatedAt TEXT NOT NULL,
            UpdatedAt TEXT NOT NULL,
            FOREIGN KEY (JobId) REFERENCES JobRuns(JobId),
            CONSTRAINT UQ_StrategyAlerts UNIQUE (TradeDate, WorkbookPath, SheetName, ExcelRow, StockCode, AlertKind)
        );
        """;
}
