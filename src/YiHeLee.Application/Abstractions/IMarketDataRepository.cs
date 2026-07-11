using YiHeLee.Domain;

namespace YiHeLee.Application.Abstractions;

/// <summary>
/// 官方每日收盤價與均線計算結果的資料存取層。
/// 只負責參數化 SQL、交易與資料轉換；日期驗證、補建與均線計算規則一律在 Service 層決定。
/// </summary>
public interface IMarketDataRepository
{
    Task InitializeAsync(CancellationToken cancellationToken = default);

    /// <summary>建立一筆官方價格批次紀錄（Pending／Running），回傳 BatchId。</summary>
    Task<string> BeginPriceBatchAsync(
        OfficialPriceJobType jobType,
        DateOnly targetDate,
        string sourceProvider,
        MarketType marketType,
        DateTimeOffset startedAt,
        CancellationToken cancellationToken);

    /// <summary>寫入批次最終狀態與統計筆數。</summary>
    Task CompletePriceBatchAsync(OfficialPriceBatchSummary summary, CancellationToken cancellationToken);

    /// <summary>
    /// 以單一交易 Upsert 股票主檔與官方每日收盤價；同一股票、同一交易日重跑時更新既有資料，不新增重複列。
    /// 回傳 (新增筆數, 更新筆數)。
    /// </summary>
    Task<(int Inserted, int Updated)> UpsertDailyPricesAsync(
        IReadOnlyList<OfficialStockPrice> prices,
        CancellationToken cancellationToken);

    /// <summary>取得指定股票在 upToDate（含）以前，依交易日期由新到舊排序的最近 N 筆有效收盤價。</summary>
    Task<IReadOnlyList<(DateOnly TradeDate, decimal ClosePrice)>> GetRecentClosePricesAsync(
        string stockCode,
        DateOnly upToDate,
        int maxTradingDays,
        CancellationToken cancellationToken);

    /// <summary>取得股票主檔中已知的市場別（TWSE=Listed／TPEx=Otc）；找不到回傳 null。</summary>
    Task<MarketType?> GetStockMarketTypeAsync(string stockCode, CancellationToken cancellationToken);

    /// <summary>批次取得多檔股票的市場別，避免逐檔查詢造成大量往返。</summary>
    Task<IReadOnlyDictionary<string, MarketType>> GetStockMarketTypesAsync(
        IReadOnlyCollection<string> stockCodes,
        CancellationToken cancellationToken);

    /// <summary>取得指定交易日已保存官方收盤價的股票代碼。每日均價前置計算以這裡的 DB 資料為輸入，不依賴客戶持股。</summary>
    Task<IReadOnlyList<string>> GetStockCodesWithDailyPriceAsync(
        DateOnly tradeDate,
        CancellationToken cancellationToken)
        => Task.FromResult<IReadOnlyList<string>>([]);

    /// <summary>寫入（或更新）均線計算結果快取，供 Excel 輸出與查詢使用。</summary>
    Task SaveMovingAverageResultsAsync(
        DateOnly tradeDate,
        IReadOnlyList<MovingAverageResult> results,
        CancellationToken cancellationToken);

    Task<IReadOnlyList<MovingAverageResult>> GetMovingAverageResultsAsync(
        DateOnly tradeDate,
        CancellationToken cancellationToken);

    /// <summary>取得指定交易日的每日均價前置資料列，供 Excel「每日五日均價策略」七欄輸出使用。</summary>
    Task<IReadOnlyList<DailyMovingAverageSnapshot>> GetMovingAverageSnapshotsAsync(
        DateOnly tradeDate,
        CancellationToken cancellationToken)
        => Task.FromResult<IReadOnlyList<DailyMovingAverageSnapshot>>([]);

    /// <summary>取得每日均價前置計算異常列（CalculationStatus != Ok），供 WinForms 頁籤顯示。</summary>
    Task<IReadOnlyList<DailyMovingAverageSnapshot>> GetMovingAverageAnomaliesAsync(
        DateOnly tradeDate,
        CancellationToken cancellationToken)
        => Task.FromResult<IReadOnlyList<DailyMovingAverageSnapshot>>([]);

    /// <summary>取得指定日期以前，資料庫已保存的（跨市場、跨股票）相異交易日筆數，用於相容既有查詢。</summary>
    Task<int> GetDistinctTradeDateCountAsync(DateOnly upToDate, int maxTradingDays, CancellationToken cancellationToken);

    /// <summary>取得指定日期以前，指定市場已保存的相異交易日筆數，用於判斷該市場歷史回補是否已足夠。</summary>
    Task<int> GetDistinctTradeDateCountAsync(DateOnly upToDate, int maxTradingDays, MarketType marketType, CancellationToken cancellationToken);

    /// <summary>取得指定日期以前，指定股票已保存的相異交易日筆數；用於興櫃持股逐檔歷史回補。</summary>
    Task<int> GetDistinctTradeDateCountAsync(DateOnly upToDate, int maxTradingDays, string stockCode, CancellationToken cancellationToken);

    /// <summary>確認 DB 是否已保存指定市場、指定交易日的正式收盤價資料；已有資料時回補不應再次抓取。</summary>
    Task<bool> HasDailyPricesAsync(DateOnly tradeDate, MarketType marketType, CancellationToken cancellationToken);

    /// <summary>確認 DB 是否已保存指定股票、指定交易日的正式收盤價資料；已有資料時逐檔回補不應再次抓取。</summary>
    Task<bool> HasDailyPriceAsync(DateOnly tradeDate, string stockCode, CancellationToken cancellationToken);

    /// <summary>取得指定日期、指定市場是否已有成功批次，用於冪等判斷（同日重跑不重複抓取）。</summary>
    Task<bool> HasSucceededBatchAsync(
        OfficialPriceJobType jobType,
        DateOnly targetDate,
        string sourceProvider,
        CancellationToken cancellationToken);

    /// <summary>
    /// 取得指定過去日期、指定來源是否已有「歷史回補」批次明確確認為休市／無交易資料。
    /// 過去日期是否為休市是歷史事實、不會再改變（例如國定假日），回補時若已確認過即可直接略過，
    /// 不需要每次執行都重新對官方來源查詢同一個過去的休市日。
    /// </summary>
    Task<bool> HasResolvedHolidayBatchAsync(
        DateOnly targetDate,
        string sourceProvider,
        CancellationToken cancellationToken);

    /// <summary>
    /// 歷史收盤價分頁查詢（供「歷史收盤價」查詢畫面使用）。MA5／20／60／120 一律以 SQL Window Function
    /// 依股票、交易日期由舊到新的 Rolling Window 計算，避免對每一列個別查詢造成 N+1。
    /// 禁止一次載入全部歷史資料，呼叫端必須提供分頁條件。
    /// </summary>
    Task<StockDailyPriceQueryResult> QueryDailyPricesAsync(
        StockDailyPriceQueryFilter filter,
        CancellationToken cancellationToken);

    /// <summary>取得資料庫目前已保存的最新交易日期；尚無資料時回傳 null。</summary>
    Task<DateOnly?> GetLatestTradeDateAsync(CancellationToken cancellationToken);

    /// <summary>
    /// 篩選出指定交易日中，哪些股票代碼「先前執行已查詢過興櫃歷史行情、確認查無資料」。
    /// 歷史資料一經確認即不會改變（例如該股票當時根本還沒開始興櫃交易），回補時應直接略過這些組合，
    /// 不需要對官方來源重複發送請求；只回傳確實已記錄過的代碼子集合，不在紀錄中的代碼一律視為尚待查詢。
    /// </summary>
    Task<IReadOnlySet<string>> GetConfirmedNoEmergingDataCodesAsync(
        DateOnly tradeDate,
        IReadOnlyCollection<string> stockCodes,
        CancellationToken cancellationToken);

    /// <summary>記錄指定交易日、指定股票代碼已查詢興櫃歷史行情但確認查無資料，供未來回補略過重複查詢。</summary>
    Task RecordConfirmedNoEmergingDataAsync(
        DateOnly tradeDate,
        IReadOnlyCollection<string> stockCodes,
        DateTimeOffset checkedAt,
        CancellationToken cancellationToken);

    /// <summary>
    /// 取得指定策略日、設定與持股組合是否已走完整個歷史回補回看範圍，但仍因股票本身歷史太短而無法補足。
    /// 已確認的歷史範圍不需在同日重跑時重新掃描；若目前資料已足夠，Service 會先以即時 DB 筆數判斷，不會只依本紀錄略過。
    /// </summary>
    Task<bool> HasExhaustedHistoricalBackfillProbeAsync(
        DateOnly targetDate,
        int requiredTradingDays,
        int maxLookbackCalendarDays,
        string stockSetKey,
        CancellationToken cancellationToken)
        => Task.FromResult(false);

    /// <summary>
    /// 記錄指定策略日、設定與持股組合已走完整個歷史回補回看範圍仍不足，供同日重跑略過重複掃描。
    /// 只有在官方來源沒有 Failed／NotPublished 等暫時性狀態時才應記錄。
    /// </summary>
    Task RecordExhaustedHistoricalBackfillProbeAsync(
        DateOnly targetDate,
        int requiredTradingDays,
        int maxLookbackCalendarDays,
        string stockSetKey,
        string insufficientSummary,
        DateTimeOffset checkedAt,
        CancellationToken cancellationToken)
        => Task.CompletedTask;

    /// <summary>
    /// 取得已確認在相同回看設定下仍補不滿歷史資料的股票。呼叫端仍應先查即時 DB 筆數；
    /// 若資料已補足，不得只因這裡有紀錄就略過正常計算。
    /// </summary>
    Task<IReadOnlySet<string>> GetKnownInsufficientHistoryStockCodesAsync(
        int requiredTradingDays,
        int maxLookbackCalendarDays,
        IReadOnlyCollection<string> stockCodes,
        CancellationToken cancellationToken)
        => Task.FromResult<IReadOnlySet<string>>(new HashSet<string>(StringComparer.OrdinalIgnoreCase));

    /// <summary>記錄已走完整個回看範圍仍不足的例外股票，供未來每日工作避免反覆回補。</summary>
    Task RecordKnownInsufficientHistoryStockCodesAsync(
        IReadOnlyList<HistoricalBackfillStockException> exceptions,
        CancellationToken cancellationToken)
        => Task.CompletedTask;
}
