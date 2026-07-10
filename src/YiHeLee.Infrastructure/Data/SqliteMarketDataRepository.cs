using System.Globalization;
using Microsoft.Data.Sqlite;
using YiHeLee.Application.Abstractions;
using YiHeLee.Domain;

namespace YiHeLee.Infrastructure.Data;

/// <summary>
/// 官方每日收盤價、均線計算快取與官方批次紀錄的 SQLite 存取層。
/// 與既有 <see cref="SqliteYiHeLeeRepository"/> 共用同一個資料庫檔案，但表格與職責完全分開：
/// 本類別只處理 StockMaster／StockDailyPrice／StockMovingAverage／OfficialPriceBatch。
/// </summary>
public sealed class SqliteMarketDataRepository : IMarketDataRepository
{
    private readonly string _connectionString;
    private readonly IClock _clock;

    public SqliteMarketDataRepository(string databasePath, IClock clock)
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
        await ExecuteNonQueryAsync(connection, null, SchemaSql, cancellationToken).ConfigureAwait(false);
        await MigrateStockDailyPriceColumnsAsync(connection, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// 既有資料庫在初次建表時只保存收盤價；本次新增歷史收盤價查詢畫面需要的選填欄位
    /// （開盤／最高／最低價、成交量、漲跌價差、是否官方資料）。CREATE TABLE IF NOT EXISTS 不會為既有資料表
    /// 補上新欄位，因此以 PRAGMA table_info 檢查後用 ALTER TABLE ADD COLUMN 安全新增，
    /// 不影響既有資料列與既有查詢相容性。
    /// </summary>
    private static async Task MigrateStockDailyPriceColumnsAsync(SqliteConnection connection, CancellationToken cancellationToken)
    {
        var existingColumns = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        await using (var command = connection.CreateCommand())
        {
            command.CommandText = "PRAGMA table_info(StockDailyPrice);";
            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                existingColumns.Add(reader.GetString(1));
            }
        }

        var columnsToAdd = new (string Name, string Definition)[]
        {
            ("OpenPrice", "NUMERIC NULL"),           // 開盤價，官方來源尚未穩定解析時保持 NULL，不得偽造
            ("HighPrice", "NUMERIC NULL"),            // 最高價
            ("LowPrice", "NUMERIC NULL"),              // 最低價
            ("TradeVolume", "NUMERIC NULL"),           // 成交股數
            ("TradeValue", "NUMERIC NULL"),            // 成交金額
            ("TransactionCount", "INTEGER NULL"),      // 成交筆數
            ("PriceChange", "NUMERIC NULL"),           // 漲跌價差
            ("IsOfficial", "INTEGER NOT NULL DEFAULT 1") // 是否官方資料，本表僅保存 TWSE／TPEx 官方來源，固定為真
        };

        foreach (var (name, definition) in columnsToAdd)
        {
            if (existingColumns.Contains(name))
            {
                continue;
            }

            await using var alterCommand = connection.CreateCommand();
            alterCommand.CommandText = $"ALTER TABLE StockDailyPrice ADD COLUMN {name} {definition};";
            await alterCommand.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    public async Task<string> BeginPriceBatchAsync(
        OfficialPriceJobType jobType,
        DateOnly targetDate,
        string sourceProvider,
        MarketType marketType,
        DateTimeOffset startedAt,
        CancellationToken cancellationToken)
    {
        var batchId = Guid.NewGuid().ToString("D");
        const string sql = """
            INSERT INTO OfficialPriceBatch
                (BatchId, JobType, TargetDate, SourceProvider, MarketType, FetchStartAt, Status, CreatedAt, UpdatedAt)
            VALUES
                ($batchId, $jobType, $targetDate, $sourceProvider, $marketType, $fetchStartAt, $status, $now, $now);
            """;

        await using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.Parameters.AddWithValue("$batchId", batchId);
        command.Parameters.AddWithValue("$jobType", (int)jobType);
        command.Parameters.AddWithValue("$targetDate", ToDate(targetDate));
        command.Parameters.AddWithValue("$sourceProvider", sourceProvider);
        command.Parameters.AddWithValue("$marketType", (int)marketType);
        command.Parameters.AddWithValue("$fetchStartAt", ToTimestamp(startedAt));
        command.Parameters.AddWithValue("$status", (int)OfficialPriceBatchStatus.Running);
        command.Parameters.AddWithValue("$now", ToTimestamp(_clock.GetTaipeiNow()));
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        return batchId;
    }

    public async Task CompletePriceBatchAsync(OfficialPriceBatchSummary summary, CancellationToken cancellationToken)
    {
        const string sql = """
            UPDATE OfficialPriceBatch
            SET SourceDataDate = $sourceDataDate,
                FetchEndAt = $fetchEndAt,
                FetchedCount = $fetchedCount,
                InsertedCount = $insertedCount,
                UpdatedCount = $updatedCount,
                SkippedCount = $skippedCount,
                FailedCount = $failedCount,
                RetryCount = $retryCount,
                Status = $status,
                ErrorMessage = $errorMessage,
                UpdatedAt = $now
            WHERE BatchId = $batchId;
            """;

        await using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.Parameters.AddWithValue("$sourceDataDate", summary.SourceDataDate is null ? DBNull.Value : ToDate(summary.SourceDataDate.Value));
        command.Parameters.AddWithValue("$fetchEndAt", summary.FetchEndAt is null ? DBNull.Value : ToTimestamp(summary.FetchEndAt.Value));
        command.Parameters.AddWithValue("$fetchedCount", summary.FetchedCount);
        command.Parameters.AddWithValue("$insertedCount", summary.InsertedCount);
        command.Parameters.AddWithValue("$updatedCount", summary.UpdatedCount);
        command.Parameters.AddWithValue("$skippedCount", summary.SkippedCount);
        command.Parameters.AddWithValue("$failedCount", summary.FailedCount);
        command.Parameters.AddWithValue("$retryCount", summary.RetryCount);
        command.Parameters.AddWithValue("$status", (int)summary.Status);
        command.Parameters.AddWithValue("$errorMessage", (object?)summary.ErrorMessage ?? DBNull.Value);
        command.Parameters.AddWithValue("$now", ToTimestamp(_clock.GetTaipeiNow()));
        command.Parameters.AddWithValue("$batchId", summary.BatchId);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<(int Inserted, int Updated)> UpsertDailyPricesAsync(
        IReadOnlyList<OfficialStockPrice> prices,
        CancellationToken cancellationToken)
    {
        if (prices.Count == 0)
        {
            return (0, 0);
        }

        var tradeDate = prices[0].TradeDate;
        if (prices.Any(x => x.TradeDate != tradeDate))
        {
            throw new InvalidOperationException("同一批官方收盤價必須是相同交易日，禁止混用不同日期寫入。");
        }

        var inserted = 0;
        var updated = 0;
        var now = ToTimestamp(_clock.GetTaipeiNow());

        await using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            foreach (var price in prices)
            {
                var stockId = await UpsertStockMasterAsync(connection, transaction, price, now, cancellationToken).ConfigureAwait(false);

                var existed = await ExecuteScalarLongAsync(
                    connection, transaction,
                    "SELECT COUNT(1) FROM StockDailyPrice WHERE StockId = $stockId AND TradeDate = $tradeDate;",
                    command =>
                    {
                        command.Parameters.AddWithValue("$stockId", stockId);
                        command.Parameters.AddWithValue("$tradeDate", ToDate(price.TradeDate));
                    },
                    cancellationToken).ConfigureAwait(false) > 0;

                const string upsertSql = """
                    INSERT INTO StockDailyPrice
                        (StockId, TradeDate, ClosePrice, SourceProvider, SourceUrl, SourceDataDate, FetchBatchId, FetchedAt, CreatedAt, UpdatedAt)
                    VALUES
                        ($stockId, $tradeDate, $closePrice, $sourceProvider, $sourceUrl, $sourceDataDate, $fetchBatchId, $fetchedAt, $now, $now)
                    ON CONFLICT(StockId, TradeDate) DO UPDATE SET
                        ClosePrice = excluded.ClosePrice,
                        SourceProvider = excluded.SourceProvider,
                        SourceUrl = excluded.SourceUrl,
                        SourceDataDate = excluded.SourceDataDate,
                        FetchBatchId = excluded.FetchBatchId,
                        FetchedAt = excluded.FetchedAt,
                        UpdatedAt = excluded.UpdatedAt;
                    """;
                await using (var command = connection.CreateCommand())
                {
                    command.Transaction = transaction;
                    command.CommandText = upsertSql;
                    command.Parameters.AddWithValue("$stockId", stockId);
                    command.Parameters.AddWithValue("$tradeDate", ToDate(price.TradeDate));
                    command.Parameters.AddWithValue("$closePrice", price.ClosePrice);
                    command.Parameters.AddWithValue("$sourceProvider", price.SourceProvider);
                    command.Parameters.AddWithValue("$sourceUrl", price.SourceUrl);
                    command.Parameters.AddWithValue("$sourceDataDate", ToDate(price.SourceDataDate));
                    command.Parameters.AddWithValue("$fetchBatchId", price.FetchBatchId);
                    command.Parameters.AddWithValue("$fetchedAt", ToTimestamp(price.FetchedAt));
                    command.Parameters.AddWithValue("$now", now);
                    await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
                }

                if (existed) updated++; else inserted++;
            }

            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            await transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
            throw;
        }

        return (inserted, updated);
    }

    public async Task<IReadOnlyList<(DateOnly TradeDate, decimal ClosePrice)>> GetRecentClosePricesAsync(
        string stockCode,
        DateOnly upToDate,
        int maxTradingDays,
        CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT p.TradeDate, p.ClosePrice
            FROM StockDailyPrice p
            INNER JOIN StockMaster s ON s.Id = p.StockId
            WHERE s.StockCode = $stockCode AND p.TradeDate <= $upToDate
            ORDER BY p.TradeDate DESC
            LIMIT $maxDays;
            """;

        await using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.Parameters.AddWithValue("$stockCode", stockCode);
        command.Parameters.AddWithValue("$upToDate", ToDate(upToDate));
        command.Parameters.AddWithValue("$maxDays", maxTradingDays);

        var result = new List<(DateOnly, decimal)>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            result.Add((
                DateOnly.ParseExact(reader.GetString(0), "yyyy-MM-dd", CultureInfo.InvariantCulture),
                reader.GetDecimal(1)));
        }

        return result;
    }

    public async Task<MarketType?> GetStockMarketTypeAsync(string stockCode, CancellationToken cancellationToken)
    {
        const string sql = "SELECT MarketType FROM StockMaster WHERE StockCode = $stockCode LIMIT 1;";
        await using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.Parameters.AddWithValue("$stockCode", stockCode);
        var value = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        return value is null or DBNull ? null : (MarketType)Convert.ToInt32(value, CultureInfo.InvariantCulture);
    }

    public async Task<IReadOnlyDictionary<string, MarketType>> GetStockMarketTypesAsync(
        IReadOnlyCollection<string> stockCodes,
        CancellationToken cancellationToken)
    {
        var result = new Dictionary<string, MarketType>(StringComparer.OrdinalIgnoreCase);
        var codes = stockCodes.Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        if (codes.Length == 0)
        {
            return result;
        }

        await using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        var parameterNames = codes.Select((_, index) => $"$code{index}").ToArray();
        command.CommandText = $"SELECT StockCode, MarketType FROM StockMaster WHERE StockCode IN ({string.Join(",", parameterNames)});";
        for (var i = 0; i < codes.Length; i++)
        {
            command.Parameters.AddWithValue(parameterNames[i], codes[i]);
        }

        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            result[reader.GetString(0)] = (MarketType)reader.GetInt32(1);
        }

        return result;
    }

    public async Task SaveMovingAverageResultsAsync(
        DateOnly tradeDate,
        IReadOnlyList<MovingAverageResult> results,
        CancellationToken cancellationToken)
    {
        if (results.Count == 0)
        {
            return;
        }

        var now = ToTimestamp(_clock.GetTaipeiNow());
        await using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            foreach (var result in results)
            {
                var stockId = await ExecuteScalarLongAsync(
                    connection, transaction,
                    "SELECT Id FROM StockMaster WHERE StockCode = $stockCode LIMIT 1;",
                    command => command.Parameters.AddWithValue("$stockCode", result.StockCode),
                    cancellationToken).ConfigureAwait(false);

                if (stockId <= 0)
                {
                    // 找不到股票主檔（尚未取得任何官方收盤價）時略過快取，Service 端仍會回報無法判斷。
                    continue;
                }

                const string sql = """
                    INSERT INTO StockMovingAverage
                        (StockId, TradeDate, ClosePrice, Ma5, Ma20, Ma60, Ma120, AvailableTradingDayCount, CalculationStatus, CreatedAt, UpdatedAt)
                    VALUES
                        ($stockId, $tradeDate, $closePrice, $ma5, $ma20, $ma60, $ma120, $availableCount, $status, $now, $now)
                    ON CONFLICT(StockId, TradeDate) DO UPDATE SET
                        ClosePrice = excluded.ClosePrice,
                        Ma5 = excluded.Ma5,
                        Ma20 = excluded.Ma20,
                        Ma60 = excluded.Ma60,
                        Ma120 = excluded.Ma120,
                        AvailableTradingDayCount = excluded.AvailableTradingDayCount,
                        CalculationStatus = excluded.CalculationStatus,
                        UpdatedAt = excluded.UpdatedAt;
                    """;
                await using var command = connection.CreateCommand();
                command.Transaction = transaction;
                command.CommandText = sql;
                command.Parameters.AddWithValue("$stockId", stockId);
                command.Parameters.AddWithValue("$tradeDate", ToDate(tradeDate));
                command.Parameters.AddWithValue("$closePrice", (object?)result.ClosePrice ?? DBNull.Value);
                command.Parameters.AddWithValue("$ma5", (object?)result.MovingAverage5 ?? DBNull.Value);
                command.Parameters.AddWithValue("$ma20", (object?)result.MovingAverage20 ?? DBNull.Value);
                command.Parameters.AddWithValue("$ma60", (object?)result.MovingAverage60 ?? DBNull.Value);
                command.Parameters.AddWithValue("$ma120", (object?)result.MovingAverage120 ?? DBNull.Value);
                command.Parameters.AddWithValue("$availableCount", result.AvailableTradingDayCount);
                command.Parameters.AddWithValue("$status", (int)result.CalculationStatus);
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

    public async Task<IReadOnlyList<MovingAverageResult>> GetMovingAverageResultsAsync(DateOnly tradeDate, CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT s.StockCode, m.TradeDate, m.ClosePrice, m.Ma5, m.Ma20, m.Ma60, m.Ma120, m.AvailableTradingDayCount, m.CalculationStatus
            FROM StockMovingAverage m
            INNER JOIN StockMaster s ON s.Id = m.StockId
            WHERE m.TradeDate = $tradeDate;
            """;

        await using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.Parameters.AddWithValue("$tradeDate", ToDate(tradeDate));

        var result = new List<MovingAverageResult>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            result.Add(new MovingAverageResult(
                reader.GetString(0),
                DateOnly.ParseExact(reader.GetString(1), "yyyy-MM-dd", CultureInfo.InvariantCulture),
                reader.IsDBNull(2) ? null : reader.GetDecimal(2),
                reader.IsDBNull(3) ? null : reader.GetDecimal(3),
                reader.IsDBNull(4) ? null : reader.GetDecimal(4),
                reader.IsDBNull(5) ? null : reader.GetDecimal(5),
                reader.IsDBNull(6) ? null : reader.GetDecimal(6),
                reader.GetInt32(7),
                (CalculationStatus)reader.GetInt32(8)));
        }

        return result;
    }

    public async Task<int> GetDistinctTradeDateCountAsync(DateOnly upToDate, int maxTradingDays, CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT COUNT(*) FROM (
                SELECT DISTINCT TradeDate FROM StockDailyPrice WHERE TradeDate <= $upToDate
                ORDER BY TradeDate DESC LIMIT $maxDays
            );
            """;
        await using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.Parameters.AddWithValue("$upToDate", ToDate(upToDate));
        command.Parameters.AddWithValue("$maxDays", maxTradingDays);
        return Convert.ToInt32(await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false), CultureInfo.InvariantCulture);
    }

    public async Task<bool> HasSucceededBatchAsync(
        OfficialPriceJobType jobType,
        DateOnly targetDate,
        string sourceProvider,
        CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT COUNT(1) FROM OfficialPriceBatch
            WHERE JobType = $jobType AND TargetDate = $targetDate AND SourceProvider = $sourceProvider AND Status = $status;
            """;
        await using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.Parameters.AddWithValue("$jobType", (int)jobType);
        command.Parameters.AddWithValue("$targetDate", ToDate(targetDate));
        command.Parameters.AddWithValue("$sourceProvider", sourceProvider);
        command.Parameters.AddWithValue("$status", (int)OfficialPriceBatchStatus.Succeeded);
        var count = Convert.ToInt32(await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false), CultureInfo.InvariantCulture);
        return count > 0;
    }

    public async Task<StockDailyPriceQueryResult> QueryDailyPricesAsync(
        StockDailyPriceQueryFilter filter,
        CancellationToken cancellationToken)
    {
        var page = Math.Max(1, filter.Page);
        var pageSize = Math.Clamp(filter.PageSize, 1, 500);
        var offset = (page - 1) * pageSize;
        var hasKeyword = !string.IsNullOrWhiteSpace(filter.Keyword);

        // MA5／20／60／120 一律以 SQL Window Function 依 StockId 分組、依 TradeDate 排序的 Rolling Window 計算，
        // 只掃描一次 StockDailyPrice，不對每一列個別查詢前 N 筆，避免 N+1。CntN < N 時代表有效交易日數不足，
        // 對應均線欄位輸出 NULL，畫面須顯示「資料不足」，不得顯示0。
        const string sql = """
            WITH Calc AS (
                SELECT
                    p.TradeDate AS TradeDate,
                    s.MarketType AS MarketType,
                    s.StockCode AS StockCode,
                    s.StockName AS StockName,
                    p.OpenPrice AS OpenPrice,
                    p.HighPrice AS HighPrice,
                    p.LowPrice AS LowPrice,
                    p.ClosePrice AS ClosePrice,
                    p.TradeVolume AS TradeVolume,
                    p.PriceChange AS PriceChange,
                    p.SourceProvider AS SourceProvider,
                    p.IsOfficial AS IsOfficial,
                    p.FetchedAt AS FetchedAt,
                    COUNT(*) OVER (PARTITION BY p.StockId ORDER BY p.TradeDate ROWS BETWEEN 4 PRECEDING AND CURRENT ROW) AS Cnt5,
                    AVG(p.ClosePrice) OVER (PARTITION BY p.StockId ORDER BY p.TradeDate ROWS BETWEEN 4 PRECEDING AND CURRENT ROW) AS Ma5,
                    COUNT(*) OVER (PARTITION BY p.StockId ORDER BY p.TradeDate ROWS BETWEEN 19 PRECEDING AND CURRENT ROW) AS Cnt20,
                    AVG(p.ClosePrice) OVER (PARTITION BY p.StockId ORDER BY p.TradeDate ROWS BETWEEN 19 PRECEDING AND CURRENT ROW) AS Ma20,
                    COUNT(*) OVER (PARTITION BY p.StockId ORDER BY p.TradeDate ROWS BETWEEN 59 PRECEDING AND CURRENT ROW) AS Cnt60,
                    AVG(p.ClosePrice) OVER (PARTITION BY p.StockId ORDER BY p.TradeDate ROWS BETWEEN 59 PRECEDING AND CURRENT ROW) AS Ma60,
                    COUNT(*) OVER (PARTITION BY p.StockId ORDER BY p.TradeDate ROWS BETWEEN 119 PRECEDING AND CURRENT ROW) AS Cnt120,
                    AVG(p.ClosePrice) OVER (PARTITION BY p.StockId ORDER BY p.TradeDate ROWS BETWEEN 119 PRECEDING AND CURRENT ROW) AS Ma120
                FROM StockDailyPrice p
                INNER JOIN StockMaster s ON s.Id = p.StockId
            ),
            Filtered AS (
                SELECT *, COUNT(*) OVER() AS TotalCount
                FROM Calc
                WHERE ($marketType IS NULL OR MarketType = $marketType)
                  AND ($startDate IS NULL OR TradeDate >= $startDate)
                  AND ($endDate IS NULL OR TradeDate <= $endDate)
                  AND ($hasKeyword = 0 OR StockCode LIKE $keywordLike OR StockName LIKE $keywordLike)
            )
            SELECT TradeDate, MarketType, StockCode, StockName, OpenPrice, HighPrice, LowPrice, ClosePrice,
                   TradeVolume, PriceChange, SourceProvider, IsOfficial, FetchedAt,
                   CASE WHEN Cnt5 >= 5 THEN Ma5 ELSE NULL END,
                   CASE WHEN Cnt20 >= 20 THEN Ma20 ELSE NULL END,
                   CASE WHEN Cnt60 >= 60 THEN Ma60 ELSE NULL END,
                   CASE WHEN Cnt120 >= 120 THEN Ma120 ELSE NULL END,
                   TotalCount
            FROM Filtered
            ORDER BY TradeDate DESC, StockCode ASC
            LIMIT $pageSize OFFSET $offset;
            """;

        await using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.Parameters.AddWithValue("$marketType", filter.Scope == MarketScope.All ? DBNull.Value : (int)ToMarketType(filter.Scope));
        command.Parameters.AddWithValue("$startDate", filter.StartDate is null ? DBNull.Value : ToDate(filter.StartDate.Value));
        command.Parameters.AddWithValue("$endDate", filter.EndDate is null ? DBNull.Value : ToDate(filter.EndDate.Value));
        command.Parameters.AddWithValue("$hasKeyword", hasKeyword ? 1 : 0);
        command.Parameters.AddWithValue("$keywordLike", hasKeyword ? $"%{filter.Keyword!.Trim()}%" : string.Empty);
        command.Parameters.AddWithValue("$pageSize", pageSize);
        command.Parameters.AddWithValue("$offset", offset);

        var rows = new List<StockDailyPriceQueryRow>();
        var totalCount = 0;
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            rows.Add(new StockDailyPriceQueryRow(
                DateOnly.ParseExact(reader.GetString(0), "yyyy-MM-dd", CultureInfo.InvariantCulture),
                (MarketType)reader.GetInt32(1),
                reader.GetString(2),
                reader.GetString(3),
                reader.IsDBNull(4) ? null : reader.GetDecimal(4),
                reader.IsDBNull(5) ? null : reader.GetDecimal(5),
                reader.IsDBNull(6) ? null : reader.GetDecimal(6),
                reader.GetDecimal(7),
                reader.IsDBNull(8) ? null : reader.GetDecimal(8),
                reader.IsDBNull(9) ? null : reader.GetDecimal(9),
                reader.GetString(10),
                reader.GetInt32(11) != 0,
                DateTimeOffset.Parse(reader.GetString(12), CultureInfo.InvariantCulture),
                reader.IsDBNull(13) ? null : reader.GetDecimal(13),
                reader.IsDBNull(14) ? null : reader.GetDecimal(14),
                reader.IsDBNull(15) ? null : reader.GetDecimal(15),
                reader.IsDBNull(16) ? null : reader.GetDecimal(16)));
            totalCount = reader.GetInt32(17);
        }

        return new StockDailyPriceQueryResult(rows, totalCount, page, pageSize);
    }

    public async Task<DateOnly?> GetLatestTradeDateAsync(CancellationToken cancellationToken)
    {
        const string sql = "SELECT MAX(TradeDate) FROM StockDailyPrice;";
        await using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        var value = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        return value is null or DBNull ? null : DateOnly.ParseExact((string)value, "yyyy-MM-dd", CultureInfo.InvariantCulture);
    }

    private static MarketType ToMarketType(MarketScope scope) => scope switch
    {
        MarketScope.Listed => MarketType.Listed,
        MarketScope.Otc => MarketType.Otc,
        _ => throw new ArgumentOutOfRangeException(nameof(scope), scope, "MarketScope.All 不應轉換為單一 MarketType。")
    };

    private static async Task<long> UpsertStockMasterAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        OfficialStockPrice price,
        string now,
        CancellationToken cancellationToken)
    {
        const string sql = """
            INSERT INTO StockMaster (StockCode, StockName, MarketType, SecurityType, IsActive, CreatedAt, UpdatedAt)
            VALUES ($stockCode, $stockName, $marketType, '一般', 1, $now, $now)
            ON CONFLICT(StockCode) DO UPDATE SET
                StockName = excluded.StockName,
                MarketType = excluded.MarketType,
                UpdatedAt = excluded.UpdatedAt
            RETURNING Id;
            """;
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        command.Parameters.AddWithValue("$stockCode", price.StockCode);
        command.Parameters.AddWithValue("$stockName", price.StockName);
        command.Parameters.AddWithValue("$marketType", (int)price.MarketType);
        command.Parameters.AddWithValue("$now", now);
        var result = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        return Convert.ToInt64(result, CultureInfo.InvariantCulture);
    }

    private static async Task<long> ExecuteScalarLongAsync(
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
        var result = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        return result is null or DBNull ? 0 : Convert.ToInt64(result, CultureInfo.InvariantCulture);
    }

    private async Task<SqliteConnection> OpenConnectionAsync(CancellationToken cancellationToken)
    {
        var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await ExecuteNonQueryAsync(connection, null, "PRAGMA foreign_keys=ON; PRAGMA busy_timeout=10000;", cancellationToken).ConfigureAwait(false);
        return connection;
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
        -- 官方股票主檔（TWSE／TPEx）：避免每日重複保存股票名稱，並記錄上市／上櫃分類。
        CREATE TABLE IF NOT EXISTS StockMaster (
            Id INTEGER NOT NULL PRIMARY KEY AUTOINCREMENT,   -- 流水號
            StockCode TEXT NOT NULL UNIQUE,                  -- 股票代碼
            StockName TEXT NOT NULL,                         -- 股票名稱（依官方最新回應更新）
            MarketType INTEGER NOT NULL,                     -- 1上市(TWSE)／2上櫃(TPEx)
            SecurityType TEXT NOT NULL DEFAULT '一般',        -- 證券類型備註（一般股票、ETF等）
            IsActive INTEGER NOT NULL DEFAULT 1,             -- 是否仍在追蹤中
            CreatedAt TEXT NOT NULL,                         -- 建立時間
            UpdatedAt TEXT NOT NULL                          -- 最後更新時間
        );

        -- 官方每日收盤價：唯一鍵為 StockId + TradeDate，同日重跑會更新既有列而非新增。
        -- Open/High/Low/TradeVolume/TradeValue/TransactionCount/PriceChange 為選填欄位：
        -- 目前 TWSE／TPEx Provider 僅穩定解析收盤價，其餘欄位保持 NULL，不得以任何方式偽造數值。
        CREATE TABLE IF NOT EXISTS StockDailyPrice (
            Id INTEGER NOT NULL PRIMARY KEY AUTOINCREMENT,   -- 流水號
            StockId INTEGER NOT NULL,                        -- 關聯 StockMaster.Id
            TradeDate TEXT NOT NULL,                         -- 交易日期（yyyy-MM-dd，官方驗證後之正式交易日）
            OpenPrice NUMERIC NULL,                          -- 開盤價（選填，來源穩定解析前保持 NULL）
            HighPrice NUMERIC NULL,                          -- 最高價（選填）
            LowPrice NUMERIC NULL,                           -- 最低價（選填）
            ClosePrice NUMERIC NOT NULL,                     -- 官方收盤價
            TradeVolume NUMERIC NULL,                        -- 成交股數（選填）
            TradeValue NUMERIC NULL,                         -- 成交金額（選填）
            TransactionCount INTEGER NULL,                   -- 成交筆數（選填）
            PriceChange NUMERIC NULL,                        -- 漲跌價差（選填）
            SourceProvider TEXT NOT NULL,                    -- 官方來源：TWSE／TPEx
            SourceUrl TEXT NOT NULL,                         -- 來源網址
            SourceDataDate TEXT NOT NULL,                    -- 來源回報的資料日期（驗證通過後應等於 TradeDate）
            FetchBatchId TEXT NOT NULL,                      -- 對應 OfficialPriceBatch.BatchId
            IsOfficial INTEGER NOT NULL DEFAULT 1,           -- 是否官方資料，本表僅保存官方來源固定為真
            FetchedAt TEXT NOT NULL,                         -- 擷取時間
            CreatedAt TEXT NOT NULL,                         -- 建立時間
            UpdatedAt TEXT NOT NULL,                         -- 最後更新時間
            FOREIGN KEY (StockId) REFERENCES StockMaster(Id),
            CONSTRAINT UQ_StockDailyPrice UNIQUE (StockId, TradeDate)
        );
        CREATE INDEX IF NOT EXISTS IX_StockDailyPrice_TradeDate ON StockDailyPrice(TradeDate);
        CREATE INDEX IF NOT EXISTS IX_StockMaster_MarketType_Code ON StockMaster(MarketType, StockCode);

        -- 均線計算結果快取：由本系統依官方收盤價自行計算，不得以鉅亨網數值取代。
        CREATE TABLE IF NOT EXISTS StockMovingAverage (
            Id INTEGER NOT NULL PRIMARY KEY AUTOINCREMENT,   -- 流水號
            StockId INTEGER NOT NULL,                        -- 關聯 StockMaster.Id
            TradeDate TEXT NOT NULL,                         -- 交易日期
            ClosePrice NUMERIC NULL,                         -- 當日官方收盤價
            Ma5 NUMERIC NULL,                                -- 5日均線（不足5個有效交易日為 NULL）
            Ma20 NUMERIC NULL,                               -- 20日均線
            Ma60 NUMERIC NULL,                                -- 60日均線
            Ma120 NUMERIC NULL,                               -- 120日均線
            AvailableTradingDayCount INTEGER NOT NULL,        -- 實際可用有效交易日數
            CalculationStatus INTEGER NOT NULL,               -- 1正常／2交易日數不足
            CreatedAt TEXT NOT NULL,
            UpdatedAt TEXT NOT NULL,
            FOREIGN KEY (StockId) REFERENCES StockMaster(Id),
            CONSTRAINT UQ_StockMovingAverage UNIQUE (StockId, TradeDate)
        );

        -- 官方價格批次紀錄：每日排程與歷史回補分開記錄，禁止混用不同日期資料。
        CREATE TABLE IF NOT EXISTS OfficialPriceBatch (
            Id INTEGER NOT NULL PRIMARY KEY AUTOINCREMENT,   -- 流水號
            BatchId TEXT NOT NULL UNIQUE,                    -- 批次識別碼
            JobType INTEGER NOT NULL,                        -- 1每日排程(DailyMarketData)／2歷史回補(HistoricalBackfill)
            TargetDate TEXT NOT NULL,                        -- 目標日期
            SourceProvider TEXT NOT NULL,                    -- TWSE／TPEx
            MarketType INTEGER NOT NULL,                     -- 1上市／2上櫃
            SourceDataDate TEXT NULL,                        -- 來源實際回報的資料日期
            FetchStartAt TEXT NOT NULL,                      -- 開始時間
            FetchEndAt TEXT NULL,                             -- 完成時間
            FetchedCount INTEGER NOT NULL DEFAULT 0,          -- 抓取筆數
            InsertedCount INTEGER NOT NULL DEFAULT 0,         -- 新增筆數
            UpdatedCount INTEGER NOT NULL DEFAULT 0,          -- 更新筆數
            SkippedCount INTEGER NOT NULL DEFAULT 0,          -- 略過筆數
            FailedCount INTEGER NOT NULL DEFAULT 0,           -- 失敗筆數
            RetryCount INTEGER NOT NULL DEFAULT 0,            -- 重試次數
            Status INTEGER NOT NULL,                          -- 批次狀態
            ErrorMessage TEXT NULL,                           -- 錯誤訊息
            CreatedAt TEXT NOT NULL,
            UpdatedAt TEXT NOT NULL
        );
        CREATE INDEX IF NOT EXISTS IX_OfficialPriceBatch_Target ON OfficialPriceBatch(TargetDate, SourceProvider, JobType);
        """;
}
