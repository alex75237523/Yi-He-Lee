using System.Globalization;
using Microsoft.Data.Sqlite;
using YiHeLee.Application.Abstractions;
using YiHeLee.Domain;

namespace YiHeLee.Infrastructure.Data;

/// <summary>CnyesCrossValidation 資料表存取層：只保存驗證紀錄，不影響 StockDailyPrice／StockMovingAverage 正式資料。</summary>
public sealed class SqliteStockPriceValidationRepository : IStockPriceValidationRepository
{
    private readonly string _connectionString;

    public SqliteStockPriceValidationRepository(string databasePath)
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
    }

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = SchemaSql;
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task SaveValidationRecordsAsync(IReadOnlyList<CnyesValidationRecord> records, CancellationToken cancellationToken)
    {
        if (records.Count == 0)
        {
            return;
        }

        var now = DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture);
        await using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            const string sql = """
                INSERT INTO CnyesCrossValidation
                    (TradeDate, MarketType, StockCode, WindowDays, CalculatedValue, CnyesValue, Difference,
                     Outcome, CnyesDataDate, SourceUrl, ValidatedAt, ErrorMessage, CreatedAt, UpdatedAt)
                VALUES
                    ($tradeDate, $marketType, $stockCode, $windowDays, $calculatedValue, $cnyesValue, $difference,
                     $outcome, $cnyesDataDate, $sourceUrl, $validatedAt, $errorMessage, $now, $now)
                ON CONFLICT(TradeDate, MarketType, StockCode, WindowDays) DO UPDATE SET
                    CalculatedValue = excluded.CalculatedValue,
                    CnyesValue = excluded.CnyesValue,
                    Difference = excluded.Difference,
                    Outcome = excluded.Outcome,
                    CnyesDataDate = excluded.CnyesDataDate,
                    SourceUrl = excluded.SourceUrl,
                    ValidatedAt = excluded.ValidatedAt,
                    ErrorMessage = excluded.ErrorMessage,
                    UpdatedAt = excluded.UpdatedAt;
                """;
            foreach (var record in records)
            {
                await using var command = connection.CreateCommand();
                command.Transaction = transaction;
                command.CommandText = sql;
                command.Parameters.AddWithValue("$tradeDate", ToDate(record.TradeDate));
                command.Parameters.AddWithValue("$marketType", (int)record.MarketType);
                command.Parameters.AddWithValue("$stockCode", record.StockCode);
                command.Parameters.AddWithValue("$windowDays", record.WindowDays);
                command.Parameters.AddWithValue("$calculatedValue", (object?)record.CalculatedValue ?? DBNull.Value);
                command.Parameters.AddWithValue("$cnyesValue", (object?)record.CnyesValue ?? DBNull.Value);
                command.Parameters.AddWithValue("$difference", (object?)record.Difference ?? DBNull.Value);
                command.Parameters.AddWithValue("$outcome", (int)record.Outcome);
                command.Parameters.AddWithValue("$cnyesDataDate", record.CnyesDataDate is null ? DBNull.Value : ToDate(record.CnyesDataDate.Value));
                command.Parameters.AddWithValue("$sourceUrl", (object?)record.SourceUrl ?? DBNull.Value);
                command.Parameters.AddWithValue("$validatedAt", record.ValidatedAt.ToString("O", CultureInfo.InvariantCulture));
                command.Parameters.AddWithValue("$errorMessage", (object?)record.ErrorMessage ?? DBNull.Value);
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

    public async Task<IReadOnlyList<CnyesValidationRecord>> GetValidationRecordsAsync(DateOnly tradeDate, CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT TradeDate, MarketType, StockCode, WindowDays, CalculatedValue, CnyesValue, Difference,
                   Outcome, CnyesDataDate, SourceUrl, ValidatedAt, ErrorMessage
            FROM CnyesCrossValidation
            WHERE TradeDate = $tradeDate
            ORDER BY MarketType, StockCode, WindowDays;
            """;
        await using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.Parameters.AddWithValue("$tradeDate", ToDate(tradeDate));

        var results = new List<CnyesValidationRecord>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            results.Add(new CnyesValidationRecord(
                DateOnly.ParseExact(reader.GetString(0), "yyyy-MM-dd", CultureInfo.InvariantCulture),
                (MarketType)reader.GetInt32(1),
                reader.GetString(2),
                reader.GetInt32(3),
                reader.IsDBNull(4) ? null : reader.GetDecimal(4),
                reader.IsDBNull(5) ? null : reader.GetDecimal(5),
                reader.IsDBNull(6) ? null : reader.GetDecimal(6),
                (CnyesValidationOutcome)reader.GetInt32(7),
                reader.IsDBNull(8) ? null : DateOnly.ParseExact(reader.GetString(8), "yyyy-MM-dd", CultureInfo.InvariantCulture),
                reader.IsDBNull(9) ? null : reader.GetString(9),
                DateTimeOffset.Parse(reader.GetString(10), CultureInfo.InvariantCulture),
                reader.IsDBNull(11) ? null : reader.GetString(11)));
        }

        return results;
    }

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

    private const string SchemaSql = """
        -- 鉅亨網多頭／空頭排列與官方自算均線的交叉驗證紀錄；僅作驗證追查，不得覆蓋或取代官方資料。
        CREATE TABLE IF NOT EXISTS CnyesCrossValidation (
            Id INTEGER NOT NULL PRIMARY KEY AUTOINCREMENT,   -- 流水號
            TradeDate TEXT NOT NULL,                         -- 交易日期
            MarketType INTEGER NOT NULL,                     -- 1上市／2上櫃
            StockCode TEXT NOT NULL,                         -- 股票代碼
            WindowDays INTEGER NOT NULL,                     -- 比對的均線天數（5／20／60／120）
            CalculatedValue NUMERIC NULL,                    -- 本系統依官方收盤價自算的均價
            CnyesValue NUMERIC NULL,                         -- 鉅亨網頁面顯示的均價
            Difference NUMERIC NULL,                         -- 絕對差異
            Outcome INTEGER NOT NULL,                        -- 驗證結果（相符／差異／不適用／日期不符／資料不足／來源失敗）
            CnyesDataDate TEXT NULL,                         -- 鉅亨網頁面實際顯示的資料日期
            SourceUrl TEXT NULL,                             -- 鉅亨網來源網址
            ValidatedAt TEXT NOT NULL,                       -- 驗證時間
            ErrorMessage TEXT NULL,                          -- 錯誤訊息
            CreatedAt TEXT NOT NULL,
            UpdatedAt TEXT NOT NULL,
            CONSTRAINT UQ_CnyesCrossValidation UNIQUE (TradeDate, MarketType, StockCode, WindowDays)
        );
        CREATE INDEX IF NOT EXISTS IX_CnyesCrossValidation_TradeDate ON CnyesCrossValidation(TradeDate);
        """;
}
