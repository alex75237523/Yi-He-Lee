using YiHeLee.Domain;

namespace YiHeLee.Application.Abstractions;

/// <summary>由 WinForms 實作，Application 層不直接依賴 UI。</summary>
public interface IUserInteraction
{
    Task<bool> ConfirmExcelSafetyAsync(CancellationToken cancellationToken);
    void ShowStatus(string message);
    void ShowSuccess(JobRunSummary summary);
    void ShowFailure(JobRunSummary summary);
}
