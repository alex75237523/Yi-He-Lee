using YiHeLee.Domain;

namespace YiHeLee.Application.Abstractions;

/// <summary>
/// 使用者於「歷史收盤價」畫面手動觸發回補批次（StockPriceImportJob／StockPriceImportTask）的進度追蹤存取層。
/// 只負責批次與工作明細的參數化 SQL 與交易，不含 HTTP、HTML／JSON 解析；實際抓取與寫入官方收盤價
/// 一律透過 <see cref="IMarketPriceService"/> 既有的單一市場＋單一交易日方法執行，避免重複實作抓取邏輯。
/// 進度百分比與彙總筆數皆由 Task 明細列即時彙總計算，不依賴記憶體計數器，重新整理／重啟後仍可由資料庫回復。
/// </summary>
public interface IStockPriceImportRepository
{
    Task InitializeAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// 建立一個回補批次與其下所有「市場＋交易日期」工作（初始狀態一律為 Queued），於執行前一次性寫入，
    /// 讓畫面重新整理即可看到完整工作清單與初始進度，不需等待實際抓取開始。
    /// </summary>
    Task<StockPriceImportJobCreationResult> CreateJobAsync(
        OfficialPriceJobType jobType,
        int requestedTradingDays,
        DateOnly? targetDate,
        string timeZoneId,
        IReadOnlyList<StockPriceImportTaskDescriptor> tasks,
        DateTimeOffset now,
        CancellationToken cancellationToken);

    Task MarkJobRunningAsync(long jobId, DateTimeOffset startedAt, CancellationToken cancellationToken);

    Task StartTaskAsync(long taskId, DateTimeOffset startedAt, CancellationToken cancellationToken);

    /// <summary>寫入單一工作最終結果，並重新彙總所屬批次的整體進度。</summary>
    Task CompleteTaskAsync(
        long taskId,
        StockPriceImportTaskStatus status,
        DateOnly? actualTradeDate,
        string? sourceUrl,
        int retryCount,
        int totalRows,
        int insertedRows,
        int updatedRows,
        int skippedRows,
        int failedRows,
        DateTimeOffset completedAt,
        string? errorMessage,
        CancellationToken cancellationToken);

    /// <summary>依批次下所有工作明細的目前狀態，計算最終批次狀態並寫入完成時間。</summary>
    Task FinalizeJobAsync(long jobId, DateTimeOffset completedAt, CancellationToken cancellationToken);

    /// <summary>使用者按下「取消抓取」：尚在排隊／執行中的工作一律標記為 Cancelled，批次狀態改為 Cancelled。</summary>
    Task CancelJobAsync(long jobId, DateTimeOffset cancelledAt, CancellationToken cancellationToken);

    Task<StockPriceImportJobProgress?> GetJobProgressAsync(long jobId, CancellationToken cancellationToken);

    Task<StockPriceImportJobProgress?> GetLatestJobProgressAsync(CancellationToken cancellationToken);

    Task<IReadOnlyList<StockPriceImportTaskProgress>> GetTaskProgressAsync(long jobId, CancellationToken cancellationToken);
}

/// <summary>建立批次後回傳的批次編號，以及每個「市場＋請求日期」對應的工作編號，供執行階段直接更新指定工作列。</summary>
public sealed record StockPriceImportJobCreationResult(
    long JobId,
    IReadOnlyDictionary<(MarketType MarketType, DateOnly RequestedDate), long> TaskIds);
