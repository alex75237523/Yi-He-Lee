using YiHeLee.Domain;

namespace YiHeLee.Application.Abstractions;

/// <summary>
/// 「歷史收盤價」畫面「立即回補」的並行回補協調服務。工作單位為「市場＋交易日期」，
/// 一次回補 N 個有效交易日時，工作數約為 N（有效交易日）× 市場數，而非每檔股票各一個請求。
/// 建立批次（快速、僅寫入 Queued 進度列）與實際執行（可能耗時、需可取消）分開，
/// 讓呼叫端（畫面）可以立即取得 JobId 開始輪詢進度，不需同步等待整批完成。
/// </summary>
public interface IStockHistoryImportService
{
    /// <summary>建立批次與其下所有「市場＋交易日期」工作（Queued），立即回傳，不進行任何 HTTP 呼叫。</summary>
    Task<long> CreateJobAsync(
        StockHistoryImportRequest request,
        StockHistoryImportOptions options,
        CancellationToken cancellationToken);

    /// <summary>
    /// 實際執行指定批次：以 <see cref="StockHistoryImportOptions.MaxConcurrency"/> 為上限並行抓取，
    /// 逐工作重試（指數退避）、逐工作更新進度，支援 <paramref name="cancellationToken"/> 取消。
    /// </summary>
    Task RunJobAsync(
        long jobId,
        OfficialMarketDataSettings marketDataSettings,
        StockHistoryImportOptions importOptions,
        CancellationToken cancellationToken);
}
