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

    /// <summary>寫入（或更新）均線計算結果快取，供 Excel 輸出與查詢使用。</summary>
    Task SaveMovingAverageResultsAsync(
        DateOnly tradeDate,
        IReadOnlyList<MovingAverageResult> results,
        CancellationToken cancellationToken);

    Task<IReadOnlyList<MovingAverageResult>> GetMovingAverageResultsAsync(
        DateOnly tradeDate,
        CancellationToken cancellationToken);

    /// <summary>取得指定日期以前，資料庫已保存的（跨股票）相異交易日筆數，用於判斷歷史回補是否已足夠。</summary>
    Task<int> GetDistinctTradeDateCountAsync(DateOnly upToDate, int maxTradingDays, CancellationToken cancellationToken);

    /// <summary>取得指定日期、指定市場是否已有成功批次，用於冪等判斷（同日重跑不重複抓取）。</summary>
    Task<bool> HasSucceededBatchAsync(
        OfficialPriceJobType jobType,
        DateOnly targetDate,
        string sourceProvider,
        CancellationToken cancellationToken);
}
