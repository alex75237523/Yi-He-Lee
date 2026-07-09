namespace YiHeLee.Domain;

public enum IndicatorType
{
    BullishAlignment = 1,
    BearishAlignment = 2
}

public enum MarketType
{
    Listed = 1,
    Otc = 2
}

public enum JobStatus
{
    Running = 1,
    Succeeded = 2,
    WebsiteNotUpdated = 3,
    NoTradingData = 4,
    CrawlFailed = 5,
    ValidationFailed = 6,
    ExcelUnavailable = 7,
    ExcelWriteFailed = 8,
    Cancelled = 9
}

public enum RunOutcome
{
    Success = 1,
    RetryableFailure = 2,
    NonRetryableFailure = 3
}

public enum AlertKind
{
    MovingAverageTriggered = 1,
    TechnicalIndicatorMissing = 2
}

/// <summary>均線計算狀態：交易日數不足時不得硬算，也不得產生該均線通知。</summary>
public enum CalculationStatus
{
    /// <summary>資料足夠，均線已正常計算。</summary>
    Ok = 1,

    /// <summary>有效交易日數不足，對應均線欄位須為 null。</summary>
    InsufficientHistory = 2
}

/// <summary>官方每日收盤價批次（TWSE／TPEx）狀態；每日排程與歷史回補共用同一組狀態。</summary>
public enum OfficialPriceBatchStatus
{
    Pending = 1,
    Running = 2,

    /// <summary>網站尚未公布 targetDate 當日資料，需依重試機制稍後重試，不得改抓前一交易日。</summary>
    NotPublished = 3,

    /// <summary>官方來源明確回報當日為休市／非交易日。</summary>
    Holiday = 4,
    Succeeded = 5,

    /// <summary>部分股票或部分來源失敗，但已成功部分仍保留紀錄以利追查。</summary>
    PartialFailed = 6,
    Failed = 7,

    /// <summary>MA120 所需的有效交易日數不足。</summary>
    InsufficientHistory = 8
}

/// <summary>官方價格批次類型：每日正式排程與歷史回補必須分開記錄，互不混淆。</summary>
public enum OfficialPriceJobType
{
    DailyMarketData = 1,
    HistoricalBackfill = 2
}
