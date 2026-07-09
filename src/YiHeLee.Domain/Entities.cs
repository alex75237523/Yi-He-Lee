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
    string? SourceUrl);

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
