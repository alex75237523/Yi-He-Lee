using YiHeLee.Domain;

namespace YiHeLee.Application.Abstractions;

/// <summary>
/// 盤中通知去重狀態（IntradayAlertState）與盤中執行紀錄（IntradayEvaluationRun）的資料存取層
/// （2026-07-13 盤中／收盤流程拆分新增）。只負責參數化 SQL、交易與資料轉換；
/// 去重規則（不成立→成立才通知、成立→不成立記錄清除）一律在 Service 層決定。
/// 盤中紀錄使用獨立資料表，不寫入 JobRuns，避免與收盤更新的 Job 語意混淆。
/// </summary>
public interface IIntradayStateRepository
{
    Task InitializeAsync(CancellationToken cancellationToken = default);

    /// <summary>取得指定判斷日、指定活頁簿的全部通知狀態；程式重啟後由此恢復，避免重複通知。</summary>
    Task<IReadOnlyList<IntradayAlertStateRecord>> GetAlertStatesAsync(
        DateOnly evaluationDate,
        string workbookPath,
        CancellationToken cancellationToken);

    /// <summary>以唯一鍵（EvaluationDate＋WorkbookPath＋SheetName＋ExcelRow＋StockCode＋AlertKind＋MaWindow）Upsert 通知狀態。</summary>
    Task UpsertAlertStatesAsync(
        IReadOnlyList<IntradayAlertStateRecord> states,
        CancellationToken cancellationToken);

    /// <summary>保存一筆盤中執行摘要（每分鐘一筆，不保存整份持股快照）。</summary>
    Task SaveEvaluationRunAsync(IntradayEvaluationRunRecord run, CancellationToken cancellationToken);

    /// <summary>取得指定判斷日最近的盤中執行紀錄（由新到舊），供畫面顯示與追查。</summary>
    Task<IReadOnlyList<IntradayEvaluationRunRecord>> GetEvaluationRunsAsync(
        DateOnly evaluationDate,
        int limit,
        CancellationToken cancellationToken);
}
