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
/// <see cref="EntryAveragePrice"/> 為表頭「進場價/平均價」欄位的值，與「現價」是完全獨立的欄位，
/// 不是 DDE 欄位，禁止與「現價」混用、互相代替或共用同一個 Issue／數值；無法判讀時為 null，
/// 原因記錄於 <see cref="EntryAveragePriceIssue"/>。
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
    string? CurrentPriceIssue = null,
    decimal? EntryAveragePrice = null,
    string? EntryAveragePriceIssue = null);

/// <summary>均線策略通知結果。<see cref="CurrentPrice"/> 為判斷當下 Excel「現價」欄位（DDE）的值；
/// AlertKind 為 CurrentPriceInvalid 時必為 null，原因寫在 <see cref="CurrentPriceIssue"/> 與 TriggerDescription。
/// <see cref="EntryAveragePrice"/>／<see cref="EntryAveragePriceIssue"/> 為表頭「進場價/平均價」欄位（非 DDE），
/// 與現價完全獨立，AlertKind 為 EntryAveragePriceInvalid 時 <see cref="EntryAveragePrice"/> 必為 null；
/// 兩組價格欄位禁止共用同一個 Issue 或同一個數值。
/// 2026-07-19 正式策略重新定義下列三個旗標（欄位沿用，語意已更新）：
/// <see cref="TriggeredMa5"/> 代表子條件「現價 &lt; MA5」是否成立；
/// <see cref="TriggeredMa20"/> 代表子條件「進場價/平均價 &gt; MA20」是否成立；
/// <see cref="TriggeredMa120"/> 固定為 false，MA120 不再參與策略。
/// 整體是否觸發只能是 <see cref="TriggeredMa5"/> &amp;&amp; <see cref="TriggeredMa20"/>（兩項同時成立），
/// 任一旗標單獨成立都不得視為觸發。
/// <see cref="DiagnosticStatus"/>／<see cref="MissingReason"/>／<see cref="AvailableTradingDayCount"/>／
/// <see cref="LatestAvailableTradeDate"/> 為 Excel 輸出診斷欄位（計算狀態、缺少原因、有效交易日數、最新收盤日期），
/// 不得讓使用者只看到空白卻不知道原因。</summary>
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
    DateTimeOffset? CalculatedAt = null,
    string? DiagnosticStatus = null,
    string? MissingReason = null,
    int AvailableTradingDayCount = 0,
    DateOnly? LatestAvailableTradeDate = null,
    decimal? EntryAveragePrice = null,
    string? EntryAveragePriceIssue = null,
    string? CurrentPriceIssue = null);

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
/// <see cref="LatestAvailableTradeDate"/>／<see cref="MissingReason"/> 為逐檔歷史完整性診斷欄位，
/// 供 Excel 輸出「最新收盤日期」「缺少原因」欄位使用，不得只留空白而沒有原因。
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
    CalculationStatus CalculationStatus,
    DateOnly? LatestAvailableTradeDate = null,
    string? MissingReason = null)
{
    public bool HasMa5 => MovingAverage5 is not null;
    public bool HasMa20 => MovingAverage20 is not null;
    public bool HasMa60 => MovingAverage60 is not null;
    public bool HasMa120 => MovingAverage120 is not null;

    /// <summary>MA120 尚缺的有效交易日數；資料已足夠時為 0。</summary>
    public int MissingTradingDaysForMa120 => Math.Max(0, 120 - AvailableTradingDayCount);
}

/// <summary>
/// 每日五日均價策略的前置資料列。這是純 DB 均價快取檢視，不含客戶、Excel 現價或 DDE 狀態。
/// </summary>
public sealed record DailyMovingAverageSnapshot(
    DateOnly TradeDate,
    string StockCode,
    string StockName,
    decimal? ClosePrice,
    decimal? MovingAverage5,
    decimal? MovingAverage20,
    decimal? MovingAverage60,
    decimal? MovingAverage120,
    CalculationStatus CalculationStatus,
    string? MissingReason);

/// <summary>
/// 已確認在指定回看設定下仍無法補足歷史均線資料的股票；用於避免每日工作因例外股票反覆回補。
/// </summary>
public sealed record HistoricalBackfillStockException(
    string StockCode,
    MarketType MarketType,
    int RequiredTradingDays,
    int MaxLookbackCalendarDays,
    int AvailableTradingDayCount,
    DateOnly TargetDate,
    string Reason,
    DateTimeOffset CheckedAt);

/// <summary>
/// 單一有效持股的完整計算結果，供 Excel「每日五日均價策略」頁籤輸出使用。
/// 與 <see cref="StrategyAlert"/> 不同：<b>每一筆有效持股都必須有一筆</b>，不論是否觸發、
/// 是否有效交易日數不足、DDE 現價是否無效，都不得因此略過或漏產生。DDE 現價異常只能讓
/// <see cref="OverallResult"/> 顯示為「現價無效，暫時無法判斷」，不得影響
/// <see cref="ClosePrice"/>／<see cref="MovingAverage5"/>／<see cref="MovingAverage20"/>／
/// <see cref="MovingAverage60"/>／<see cref="MovingAverage120"/> 等已由官方收盤價算出的欄位。
/// <see cref="EntryAveragePrice"/>／<see cref="EntryAveragePriceStatus"/>／<see cref="EntryAveragePriceIssue"/>
/// 為表頭「進場價/平均價」欄位（非 DDE），與現價完全獨立，禁止與現價混用、共用同一個 Issue 或數值。
/// 2026-07-19 正式策略重新定義下列三個旗標（欄位沿用，語意已更新）：
/// <see cref="Ma5Match"/> 代表子條件「現價 &lt; MA5」是否成立；
/// <see cref="Ma20Match"/> 代表子條件「進場價/平均價 &gt; MA20」是否成立；
/// <see cref="Ma120Match"/> 固定為 false，MA120 不再參與策略。
/// <see cref="OverallResult"/> 為「觸發」只能是 <see cref="Ma5Match"/> &amp;&amp; <see cref="Ma20Match"/>（兩項同時成立）。
/// <see cref="StrategyAlert"/> 只負責中央通知與需要提醒使用者的子集合，Excel 完整結果一律以本型別為準。
/// </summary>
public sealed record HoldingStrategyResult(
    DateOnly TradeDate,
    string CustomerName,
    string SheetName,
    int ExcelRow,
    string RawStockCode,
    string ResolvedStockCode,
    string StockName,
    MarketType? MarketType,
    decimal? EntryAveragePrice,
    string EntryAveragePriceStatus,
    string? EntryAveragePriceIssue,
    decimal? CurrentPrice,
    string CurrentPriceStatus,
    string? CurrentPriceIssue,
    decimal? ClosePrice,
    decimal? MovingAverage5,
    decimal? MovingAverage20,
    decimal? MovingAverage60,
    decimal? MovingAverage120,
    int AvailableTradingDayCount,
    DateOnly? LatestAvailableTradeDate,
    string CalculationStatus,
    string? MissingReason,
    string CnyesValidationStatus,
    bool Ma5Match,
    bool Ma20Match,
    bool Ma120Match,
    string OverallResult,
    string TriggerDescription,
    DateTimeOffset CalculatedAt);

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

/// <summary>
/// 單一股票代碼的正規化與官方身分解析結果，由 <c>StockIdentityResolutionService</c> 產生。
/// <see cref="ResolvedCode"/> 可能與 <see cref="RawCode"/> 不同（例如 Excel 讀到「50」，
/// 經官方主檔確認後解析為「0050」）；補零前一律先確認官方主檔存在對應代碼，不得盲目補零。
/// </summary>
public sealed record StockCodeResolution(
    string RawCode,
    string ResolvedCode,
    MarketType? MarketType,
    StockIdentity Identity,
    bool IsRecognized,
    string? UnrecognizedReason);

/// <summary>
/// 盤中判斷使用的「上一交易日均價基準」解析結果（2026-07-13 盤中／收盤流程拆分新增）。
/// <see cref="BaselineTradeDate"/> 一律由已保存的官方收盤資料解析出「真正的上一交易日」，
/// 禁止以 <c>today.AddDays(-1)</c> 推算；快照不存在、不完整或上一交易日收盤更新未成功時
/// <see cref="IsReady"/> 為 false，並以 <see cref="NotReadyReason"/> 說明原因，
/// 禁止自動退回更舊的均價資料冒充上一交易日。
/// </summary>
public sealed record IntradayBaselineResolution(
    DateOnly EvaluationDate,
    DateOnly? BaselineTradeDate,
    bool IsReady,
    string? NotReadyReason,
    DateOnly? LatestPriceTradeDate,
    DateOnly? LatestMovingAverageTradeDate,
    DateOnly? ExpectedBaselineTradeDate = null);

/// <summary>
/// 盤中基準資料準備狀態。這是由既有 SQLite 資料推導出的狀態快照，不要求新增資料表：
/// <see cref="OfficialPriceReady"/> 代表基準日已有官方收盤價；<see cref="MovingAverageReady"/> 代表
/// 同一批股票皆已有完整 MA5／MA20／MA60／MA120 快照。
/// </summary>
public sealed record BaselinePreparationState(
    DateOnly EvaluationDate,
    DateOnly? BaselineTradeDate,
    bool CalendarResolved,
    bool OfficialPriceReady,
    bool MovingAverageReady,
    BaselinePreparationStatus Status,
    DateTimeOffset? CompletedAt,
    string? LastError,
    int OfficialPriceStockCount,
    int MovingAverageStockCount);

/// <summary>
/// 單次 EnsureBaselineDataAsync 的結果；<see cref="PreparedThisRun"/> 為 true 時代表本次呼叫內曾補資料或重算均價，
/// 呼叫端必須在同一次盤中判斷中重新解析基準並繼續讀取 Excel，不得要求使用者再按第二次。
/// </summary>
public sealed record BaselinePreparationResult(
    BaselinePreparationState State,
    bool PreparedThisRun,
    bool IsAnotherPreparationRunning,
    string Message);

/// <summary>
/// 盤中通知去重狀態（IntradayAlertState 資料表，2026-07-13 新增）。
/// 同一條件持續成立時只在「由不成立變成立」時通知一次；成立→不成立記錄 <see cref="ClearedAt"/>；
/// 之後再次成立可再次通知。程式重啟後由 SQLite 恢復狀態，不得對仍持續成立的條件重複通知。
/// <see cref="MaWindow"/>：2026-07-19 起 MA5＋MA20 複合策略與其他非單一均線通知一律為 0；
/// 舊版的 5／20／120（依單一均線分開）已作廢，殘留狀態第一次執行新版時會被清除。
/// </summary>
public sealed record IntradayAlertStateRecord(
    DateOnly EvaluationDate,
    DateOnly BaselineTradeDate,
    string WorkbookPath,
    string SheetName,
    int ExcelRow,
    string StockCode,
    AlertKind AlertKind,
    int MaWindow,
    bool IsActive,
    DateTimeOffset FirstTriggeredAt,
    DateTimeOffset LastEvaluatedAt,
    DateTimeOffset? LastNotifiedAt,
    DateTimeOffset? ClearedAt);

/// <summary>
/// 盤中自動判斷執行紀錄（IntradayEvaluationRun 資料表，2026-07-13 新增）。
/// 每次 Tick 只保存摘要，不重複保存整份持股快照；與收盤更新的 JobRuns 完全分開，語意不得混用。
/// <see cref="EvaluationDate"/> 為今天盤中判斷日期，<see cref="BaselineTradeDate"/> 為使用的上一交易日均價日期，
/// 兩者必須明確分開，禁止只用一個日期同時代表兩種語意。
/// </summary>
public sealed record IntradayEvaluationRunRecord(
    long Id,
    DateOnly EvaluationDate,
    DateOnly? BaselineTradeDate,
    DateTimeOffset ScheduledAt,
    DateTimeOffset? StartedAt,
    DateTimeOffset? CompletedAt,
    IntradayRunStatus Status,
    int HoldingCount,
    int TriggeredCount,
    int NewNotificationCount,
    int EntryAveragePriceInvalidCount,
    int CurrentPriceInvalidCount,
    int MissingMovingAverageCount,
    string? SkippedReason,
    string? ErrorMessage);

/// <summary>
/// 盤中單次判斷的彙整結果，供中央結果畫面、系統匣與 Log 顯示。
/// 通知、Log 與畫面都必須能同時看到 <see cref="EvaluationDate"/>（今天判斷的是哪一天）
/// 與 <see cref="BaselineTradeDate"/>（使用哪一個交易日的均價）。
/// <see cref="NewlyTriggeredAlerts"/> 只包含本次由「不成立變成立」的新通知（去重後），
/// <see cref="Alerts"/> 為本次判斷的完整清單（含持續成立與異常）。
/// </summary>
public sealed record IntradayRunSummary(
    DateOnly EvaluationDate,
    DateOnly? BaselineTradeDate,
    DateTimeOffset ScheduledAt,
    DateTimeOffset EvaluatedAt,
    bool IsManualRun,
    IntradayRunStatus Status,
    string Message,
    int HoldingCount,
    int ActiveTriggerCount,
    int NewNotificationCount,
    int EntryAveragePriceInvalidCount,
    int CurrentPriceInvalidCount,
    int MissingMovingAverageCount,
    IReadOnlyList<StrategyAlert> Alerts,
    IReadOnlyList<StrategyAlert> NewlyTriggeredAlerts);

/// <summary>
/// 市場工作流程目前狀態快照（2026-07-13 新增），供主視窗狀態列與系統匣文字顯示；
/// 系統匣不得再只顯示「等待每日 13:35」，必須依時段顯示盤中監控／等待收盤／已完成／基準未就緒。
/// </summary>
public sealed record MarketWorkflowStatusSnapshot(
    MarketWorkflowPhase Phase,
    DateOnly EvaluationDate,
    DateOnly? BaselineTradeDate,
    DateTimeOffset? LastIntradayEvaluatedAt,
    DateTimeOffset? NextIntradayTickAt,
    DateOnly? LastCloseSucceededDate,
    DateTimeOffset? NextCloseRunAt,
    int HoldingCount,
    int ActiveTriggerCount,
    int NewNotificationCount,
    int EntryAveragePriceInvalidCount,
    int CurrentPriceInvalidCount,
    string StatusText);

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
    IReadOnlyList<StrategyAlert> Alerts,
    IReadOnlyList<DailyMovingAverageSnapshot>? MovingAverageAnomalies = null);
