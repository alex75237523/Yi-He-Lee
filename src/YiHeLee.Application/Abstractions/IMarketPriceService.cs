using YiHeLee.Domain;

namespace YiHeLee.Application.Abstractions;

/// <summary>
/// 官方每日收盤價協調服務：驗證台北日期與來源資料日期一致、處理休市／尚未公布狀態、
/// 呼叫 Repository 以冪等方式 Upsert，並記錄批次狀態。排程與 UI 不得繞過本服務直接呼叫 Provider。
/// </summary>
public interface IMarketPriceService
{
    /// <summary>
    /// 抓取並保存 targetDate 當日 TWSE、TPEx 官方收盤價。
    /// 任一來源資料日期與 targetDate 不一致時，該來源批次不得寫入正式資料，並回報 NotPublished。
    /// </summary>
    Task<IReadOnlyList<OfficialPriceBatchSummary>> FetchAndSaveDailyPricesAsync(
        DateOnly targetDate,
        OfficialMarketDataSettings settings,
        CancellationToken cancellationToken);

    /// <summary>
    /// 由 targetDate 往前逐日回補官方收盤價，直到資料庫累積達 requiredTradingDays 個有效交易日，
    /// 或達到 maxLookbackCalendarDays 日曆日回看上限為止。歷史資料一律保存原始 TradeDate，不得改寫為 targetDate。
    /// </summary>
    Task<IReadOnlyList<OfficialPriceBatchSummary>> BackfillHistoryAsync(
        DateOnly targetDate,
        OfficialMarketDataSettings settings,
        CancellationToken cancellationToken);
}
