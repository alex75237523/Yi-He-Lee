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

    /// <summary>興櫃（TPEx 興櫃股票市場）。當日使用官方快照，歷史回補使用官方個股月份歷史行情。</summary>
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
    TechnicalIndicatorMissing = 2,

    /// <summary>Excel「現價」欄位（外部 DDE 連結）為錯誤值、空白、0 或無法解析，無法進行均線判斷。</summary>
    CurrentPriceInvalid = 3,

    /// <summary>
    /// Excel「進場價/平均價」欄位為 Excel 錯誤值、空白、0、負數或無法解析，無法進行均線判斷。
    /// 此欄位不是 DDE 欄位，異常原因與「現價」（<see cref="CurrentPriceInvalid"/>）分開判斷、分開顯示，
    /// 不得共用同一個原因或互相代替。
    /// </summary>
    EntryAveragePriceInvalid = 4
}

/// <summary>均線計算狀態：交易日數不足時不得硬算，也不得產生該均線通知。</summary>
public enum CalculationStatus
{
    /// <summary>資料足夠，均線已正常計算。</summary>
    Ok = 1,

    /// <summary>有效交易日數不足（逐檔檢查後仍不足），對應均線欄位須為 null。</summary>
    InsufficientHistory = 2,

    /// <summary>最新一筆官方收盤價日期不等於指定策略日期，當日收盤價尚未取得，不得以昨日資料替代。</summary>
    TodayCloseMissing = 3,

    /// <summary>逐檔歷史回補過程中發生錯誤（例如官方來源逾時或格式異常）導致資料缺口未補齊，非單純交易日不足。</summary>
    BackfillFailed = 4
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

/// <summary>
/// 盤中自動判斷單次執行狀態（2026-07-13 盤中／收盤流程拆分新增）。
/// 盤中執行紀錄使用獨立的 IntradayEvaluationRun 資料表，與收盤更新的 JobRuns 語意分開，不得混用。
/// </summary>
public enum IntradayRunStatus
{
    /// <summary>本次盤中判斷完整成功。</summary>
    Succeeded = 1,

    /// <summary>本次盤中判斷完成，但有部分持股的進場價/平均價、現價（DDE）或基準均價無法判讀，僅影響該持股。</summary>
    PartialSuccess = 2,

    /// <summary>本次盤中判斷失敗（例如 Excel 活頁簿完全無法存取）；不影響下一分鐘，也不得啟動官方資料抓取。</summary>
    Failed = 3,

    /// <summary>本次 Tick 直接略過（上一次盤中判斷尚未完成或收盤更新執行中），不得排隊累積。</summary>
    Skipped = 4,

    /// <summary>上一交易日均價基準資料尚未就緒（快照不存在、不完整或收盤更新失敗），禁止退回更舊均價，本次不判斷。</summary>
    BaselineNotReady = 5
}

/// <summary>
/// 市場工作流程目前時段狀態（2026-07-13 盤中／收盤流程拆分新增），供主視窗與系統匣顯示。
/// </summary>
public enum MarketWorkflowPhase
{
    /// <summary>盤中監控執行中（09:00～13:30，依 IntradayCheckIntervalSeconds 判斷）。</summary>
    IntradayMonitoring = 1,

    /// <summary>盤中監控已結束，等待 13:35 收盤更新。</summary>
    WaitingForClose = 2,

    /// <summary>今日收盤更新已完成。</summary>
    CloseCompleted = 3,

    /// <summary>基準均價資料未就緒，盤中監控暫停判斷。</summary>
    BaselineNotReady = 4,

    /// <summary>非交易日（週末或已確認休市），不啟動盤中監控。</summary>
    NonTradingDay = 5,

    /// <summary>非交易時段（開盤前）。</summary>
    OutsideSchedule = 6,

    /// <summary>排程已由使用者設定停用。</summary>
    Disabled = 7
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
