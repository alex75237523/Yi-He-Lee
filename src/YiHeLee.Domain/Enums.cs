namespace YiHeLee.Domain;

public enum IndicatorType
{
    BullishAlignment = 1,
    BearishAlignment = 2
}

public enum MarketType
{
    Listed = 1,
    Otc = 2,

    /// <summary>興櫃（TPEx 興櫃股票市場）。當日行情僅有官方即時快照，無日期參數可回補歷史資料，
    /// 因此只參與每日排程，不參與 <see cref="OfficialPriceJobType.HistoricalBackfill"/>。</summary>
    Emerging = 3
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

/// <summary>歷史收盤價查詢／回補的市場別篩選範圍。</summary>
public enum MarketScope
{
    All = 0,
    Listed = 1,
    Otc = 2
}

/// <summary>使用者手動觸發的歷史收盤價回補批次（StockPriceImportJob）狀態。</summary>
public enum StockPriceImportJobStatus
{
    Queued = 1,
    Running = 2,
    WaitingForSource = 3,
    Completed = 4,
    CompletedWithErrors = 5,
    Failed = 6,
    Cancelled = 7
}

/// <summary>一個「市場＋交易日期」抓取工作（StockPriceImportTask）狀態。</summary>
public enum StockPriceImportTaskStatus
{
    Queued = 1,
    Running = 2,
    WaitingForSource = 3,
    Succeeded = 4,
    Holiday = 5,
    Failed = 6,
    Cancelled = 7
}

/// <summary>鉅亨網交叉驗證結果。股票未出現在多頭／空頭清單中時為 NotApplicable，不代表計算錯誤。</summary>
public enum CnyesValidationOutcome
{
    Matched = 1,
    Mismatched = 2,
    NotApplicable = 3,

    /// <summary>鉅亨頁面日期與目標交易日期不一致，拒絕比對。</summary>
    SourceDateMismatch = 4,

    /// <summary>本系統均線資料不足（例如僅回補5日時的 MA20／60／120），不得硬算比對。</summary>
    InsufficientHistory = 5,

    /// <summary>鉅亨網來源本次擷取失敗，不影響官方資料，僅記錄無法驗證。</summary>
    SourceUnavailable = 6
}
