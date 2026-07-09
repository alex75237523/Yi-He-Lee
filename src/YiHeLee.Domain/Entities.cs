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

/// <summary>從 Excel 客戶頁籤讀取的有效持股。</summary>
public sealed record CustomerHolding(
    DateOnly SnapshotDate,
    string WorkbookPath,
    string SheetName,
    string CustomerName,
    int ExcelRow,
    string StockCode,
    string StockName,
    decimal EntryAveragePrice,
    decimal? Quantity,
    string HoldingKey);

/// <summary>均線策略通知結果。</summary>
public sealed record StrategyAlert(
    DateOnly TradeDate,
    AlertKind AlertKind,
    string WorkbookPath,
    string SheetName,
    string CustomerName,
    int ExcelRow,
    string StockCode,
    string StockName,
    decimal EntryAveragePrice,
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
