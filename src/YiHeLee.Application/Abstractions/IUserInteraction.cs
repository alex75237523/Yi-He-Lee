using YiHeLee.Domain;

namespace YiHeLee.Application.Abstractions;

/// <summary>由 WinForms 實作，Application 層不直接依賴 UI。</summary>
public interface IUserInteraction
{
    Task<bool> ConfirmExcelSafetyAsync(CancellationToken cancellationToken);

    /// <summary>回報目前狀態。percentComplete 為 0～100 的整體執行進度，畫面預設只呈現進度條，
    /// 不直接顯示 message 內容，避免讓使用者得知背後實際執行到哪個步驟。</summary>
    void ShowStatus(string message, int percentComplete = 0);

    /// <summary>回報長時間作業的細節進度（例如 MA120 歷史回補的逐日抓取進度），
    /// 一律顯示於進度條下方，不受 ShowStatusText 旗標影響；傳入空字串代表清除細節顯示。</summary>
    void ShowProgressDetail(string message);
    void ShowSuccess(JobRunSummary summary);
    void ShowFailure(JobRunSummary summary);
}
