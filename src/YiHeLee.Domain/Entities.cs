namespace YiHeLee.Domain;

/// <summary>爬文來源定義；不同網站可由不同 ProviderKey 對應專用爬蟲。</summary>
public sealed record SourceDefinition(
    string SourceKey,
    string DisplayName,
    Uri Url,
    IndicatorType IndicatorType,
    string ProviderKey,
    bool Enabled,
    bool Required);

/// <summary>每日技術指標資料。</summary>
public sealed record TechnicalIndicator(
    DateOnly TradeDate,
    IndicatorType IndicatorType,
    MarketType MarketType,
    string StockCode,
    string StockName,
    decimal ClosePrice,
    decimal MovingAverage5,
    decimal MovingAverage20,
    decimal MovingAverage60,
    decimal MovingAverage120,
    string SourceUrl,
    DateTimeOffset FetchStartedAt,
    DateTimeOffset FetchCompletedAt);

/// <summary>單一來源與市場的完整爬取結果。</summary>
public sealed record CrawlBatch(
    SourceDefinition Source,
    MarketType MarketType,
    DateOnly TargetDate,
    DateOnly PageDate,
    IReadOnlyList<TechnicalIndicator> Items,
    DateTimeOffset StartedAt,
    DateTimeOffset CompletedAt,
    bool IsExplicitNoData,
    string? RawStatusText = null);

/// <summary>
/// 從 Excel 客戶頁籤讀取的持股。<see cref="CurrentPrice"/> 為表頭「現價」欄位的值，來源是外部 DDE 連結，
/// 可能出現 #N/A 等錯誤值、空白、0 或文字；無法判讀時 <see cref="CurrentPrice"/> 為 null，
/// 並以 <see cref="CurrentPriceIssue"/> 記錄原因，策略層必須轉為「現價異常」通知，不得靜默略過。
/// </summary>
public sealed record CustomerHolding(
    DateOnly SnapshotDate,
    string WorkbookPath,
    string SheetName,
    string CustomerName,
    int ExcelRow,
    string StockCode,
    string StockName,
    decimal? CurrentPrice,
    decimal? Quantity,
    string HoldingKey,
    string? CurrentPriceIssue = null);

/// <summary>均線策略通知結果。<see cref="CurrentPrice"/> 為判斷當下 Excel「現價」欄位（DDE）的值；
/// AlertKind 為 CurrentPriceInvalid 時必為 null，原因寫在 TriggerDescription。</summary>
public sealed record StrategyAlert(
    DateOnly TradeDate,
    AlertKind AlertKind,
    string WorkbookPath,
    string SheetName,
    string CustomerName,
    int ExcelRow,
    string StockCode,
    string StockName,
    decimal? CurrentPrice,
    decimal? Quantity,
    decimal? ClosePrice,
    decimal? MovingAverage5,
    decimal? MovingAverage20,
    decimal? MovingAverage60,
    decimal? MovingAverage120,
    bool TriggeredMa5,
    bool TriggeredMa20,
    bool TriggeredMa120,
    string TriggerDescription,
    MarketType? MarketType,
    IndicatorType? IndicatorType,
    string? SourceUrl,
    string? PriceSourceProvider = null,
    DateTimeOffset? CalculatedAt = null);

/// <summary>
/// TWSE／TPEx 官方來源單一股票、單一交易日的原始收盤價（Provider 輸出的來源 DTO）。
/// Provider 只負責回報這筆資料與「來源自己回報的資料日期」，日期是否等於 targetDate 由 Service 驗證。
/// </summary>
public sealed record OfficialPriceQuote(
    string StockCode,
    string StockName,
    decimal ClosePrice);

/// <summary>
/// 官方來源單一市場別、單一（來源回報的）資料日期的完整查詢結果。
/// </summary>
public sealed record OfficialPriceFetchResult(
    MarketType MarketType,
    DateOnly RequestedDate,
    DateOnly? SourceDataDate,
    IReadOnlyList<OfficialPriceQuote> Quotes,
    bool IsHolidayOrNoData,
    string SourceProvider,
    string SourceUrl,
    DateTimeOffset FetchStartedAt,
    DateTimeOffset FetchCompletedAt);

/// <summary>官方每日收盤價，經 Service 驗證來源日期等於交易日後才可寫入正式資料表。</summary>
public sealed record OfficialStockPrice(
    string StockCode,
    string StockName,
    MarketType MarketType,
    DateOnly TradeDate,
    decimal ClosePrice,
    string SourceProvider,
    string SourceUrl,
    DateOnly SourceDataDate,
    string FetchBatchId,
    DateTimeOffset FetchedAt);

/// <summary>
/// 由本系統依官方收盤價自行計算的均線結果。CalculationStatus 為 InsufficientHistory 時，
/// 對應天數的均線欄位必須是 null，不得以較少天數硬算，也不得產生該均線通知。
/// </summary>
public sealed record MovingAverageResult(
    string StockCode,
    DateOnly TradeDate,
    decimal? ClosePrice,
    decimal? MovingAverage5,
    decimal? MovingAverage20,
    decimal? MovingAverage60,
    decimal? MovingAverage120,
    int AvailableTradingDayCount,
    CalculationStatus CalculationStatus);

/// <summary>官方價格批次（每日排程或歷史回補）執行紀錄。</summary>
public sealed record OfficialPriceBatchSummary(
    string BatchId,
    OfficialPriceJobType JobType,
    DateOnly TargetDate,
    string SourceProvider,
    MarketType MarketType,
    DateOnly? SourceDataDate,
    DateTimeOffset FetchStartAt,
    DateTimeOffset? FetchEndAt,
    int FetchedCount,
    int InsertedCount,
    int UpdatedCount,
    int SkippedCount,
    int FailedCount,
    int RetryCount,
    OfficialPriceBatchStatus Status,
    string? ErrorMessage);

/// <summary>
/// 使用者於歷史收盤價畫面觸發「立即回補」的請求參數。若同時指定 <see cref="StartDate"/> 與
/// <see cref="EndDate"/>，回補範圍改以此日期區間為準（含首尾），忽略 <see cref="TradingDays"/>；
/// 否則沿用「最近 N 個交易日」計算方式。
/// </summary>
public sealed record StockHistoryImportRequest(
    MarketScope Scope,
    int TradingDays,
    DateOnly? StartDate = null,
    DateOnly? EndDate = null);

/// <summary>一個抓取工作單位：市場＋交易日期（RequestedDate 為請求日期，非官方回報的實際交易日期）。</summary>
public sealed record StockPriceImportTaskDescriptor(
    MarketType MarketType,
    DateOnly RequestedDate);

/// <summary>StockPriceImportJob 批次整體進度，供畫面顯示與重新整理後回復。</summary>
public sealed record StockPriceImportJobProgress(
    long JobId,
    OfficialPriceJobType JobType,
    int RequestedTradingDays,
    DateOnly? TargetDate,
    string TimeZoneId,
    int TotalTasks,
    int CompletedTasks,
    int SuccessTasks,
    int FailedTasks,
    int SkippedTasks,
    int TotalRows,
    int ProcessedRows,
    int InsertedRows,
    int UpdatedRows,
    int SkippedRows,
    int FailedRows,
    decimal ProgressPercent,
    StockPriceImportJobStatus Status,
    DateTimeOffset? StartedAt,
    DateTimeOffset? CompletedAt,
    string? ErrorMessage);

/// <summary>StockPriceImportTask 單一「市場＋交易日期」工作明細進度。</summary>
public sealed record StockPriceImportTaskProgress(
    long TaskId,
    long JobId,
    MarketType MarketType,
    DateOnly RequestedDate,
    DateOnly? ActualTradeDate,
    string? SourceUrl,
    StockPriceImportTaskStatus Status,
    int RetryCount,
    int TotalRows,
    int ProcessedRows,
    int InsertedRows,
    int UpdatedRows,
    int SkippedRows,
    int FailedRows,
    decimal ProgressPercent,
    DateTimeOffset? StartedAt,
    DateTimeOffset? CompletedAt,
    string? ErrorMessage);

/// <summary>歷史收盤價查詢條件；分頁與參數化查詢由 Repository 負責，禁止一次載入全部歷史資料。</summary>
public sealed record StockDailyPriceQueryFilter(
    MarketScope Scope,
    string? Keyword,
    DateOnly? StartDate,
    DateOnly? EndDate,
    int Page,
    int PageSize);

/// <summary>歷史收盤價查詢單列結果，MA 欄位為 null 時畫面須顯示「資料不足」，不得顯示0。</summary>
public sealed record StockDailyPriceQueryRow(
    DateOnly TradeDate,
    MarketType MarketType,
    string StockCode,
    string StockName,
    decimal? OpenPrice,
    decimal? HighPrice,
    decimal? LowPrice,
    decimal ClosePrice,
    decimal? TradeVolume,
    decimal? PriceChange,
    string SourceProvider,
    bool IsOfficial,
    DateTimeOffset FetchedAt,
    decimal? MovingAverage5,
    decimal? MovingAverage20,
    decimal? MovingAverage60,
    decimal? MovingAverage120);

/// <summary>歷史收盤價分頁查詢結果。</summary>
public sealed record StockDailyPriceQueryResult(
    IReadOnlyList<StockDailyPriceQueryRow> Rows,
    int TotalCount,
    int Page,
    int PageSize);

/// <summary>鉅亨網多頭／空頭排列與官方自算均線的交叉驗證紀錄。</summary>
public sealed record CnyesValidationRecord(
    DateOnly TradeDate,
    MarketType MarketType,
    string StockCode,
    int WindowDays,
    decimal? CalculatedValue,
    decimal? CnyesValue,
    decimal? Difference,
    CnyesValidationOutcome Outcome,
    DateOnly? CnyesDataDate,
    string? SourceUrl,
    DateTimeOffset ValidatedAt,
    string? ErrorMessage);

public sealed record JobRunSummary(
    Guid JobId,
    DateOnly TargetDate,
    JobStatus Status,
    RunOutcome Outcome,
    string Message,
    int AttemptNumber,
    int CrawledCount,
    int HoldingCount,
    int AlertCount,
    int MissingIndicatorCount,
    DateTimeOffset StartedAt,
    DateTimeOffset CompletedAt,
    IReadOnlyList<StrategyAlert> Alerts);
