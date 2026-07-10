using YiHeLee.Application.Abstractions;
using YiHeLee.Domain;

namespace YiHeLee.App.Infrastructure;

internal sealed class WinFormsUserInteraction : IUserInteraction
{
    private Control? _dispatcher;

    public event Action<string, int>? StatusChanged;
    public event Action<string>? ProgressDetailChanged;
    public event Action<JobRunSummary>? Succeeded;
    public event Action<JobRunSummary>? Failed;

    /// <summary>由 TrayApplicationContext 掛上，改在常駐主視窗內以內嵌畫面確認，不再另外跳出對話框。
    /// 未掛上時（例如主視窗尚未初始化）退回原本的 MessageBox，確保安全確認不會被跳過。</summary>
    public Func<CancellationToken, Task<bool>>? ExcelSafetyConfirmationHandler { get; set; }

    public void AttachDispatcher(Control dispatcher)
    {
        _dispatcher = dispatcher;
        if (!dispatcher.IsHandleCreated)
        {
            dispatcher.CreateControl();
        }
    }

    public Task<bool> ConfirmExcelSafetyAsync(CancellationToken cancellationToken)
        => InvokeAsync(async () =>
        {
            if (ExcelSafetyConfirmationHandler is not null)
            {
                return await ExcelSafetyConfirmationHandler(cancellationToken).ConfigureAwait(true);
            }

            const string message =
                "系統即將讀取並更新 Excel。\r\n\r\n" +
                "請先確認：\r\n" +
                "1. 指定活頁簿已用桌面版 Excel 開啟。\r\n" +
                "2. 已按 Enter 或 Esc，結束儲存格編輯。\r\n" +
                "3. 已關閉另存新檔、列印、尋找取代及其他 Excel 對話框。\r\n" +
                "4. 更新期間請勿關閉 Excel、另存新檔、執行巨集或改名／刪除輸出頁籤。\r\n\r\n" +
                "注意：程式完成時會儲存整份活頁簿，也會一併儲存您尚未儲存的變更。\r\n\r\n" +
                "是否開始？";
            return MessageBox.Show(message, "Yi He Lee－Excel 更新前確認",
                MessageBoxButtons.OKCancel, MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2) == DialogResult.OK;
        }, cancellationToken);

    public void ShowStatus(string message, int percentComplete = 0) => Post(() => StatusChanged?.Invoke(message, percentComplete));
    public void ShowProgressDetail(string message) => Post(() => ProgressDetailChanged?.Invoke(message));
    public void ShowSuccess(JobRunSummary summary) => Post(() => Succeeded?.Invoke(summary));
    public void ShowFailure(JobRunSummary summary) => Post(() => Failed?.Invoke(summary));

    private Task<T> InvokeAsync<T>(Func<T> action, CancellationToken cancellationToken)
    {
        var dispatcher = _dispatcher ?? throw new InvalidOperationException("UI dispatcher 尚未初始化。");
        var completion = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        cancellationToken.Register(() => completion.TrySetCanceled(cancellationToken));
        dispatcher.BeginInvoke(new Action(() =>
        {
            try { completion.TrySetResult(action()); }
            catch (Exception ex) { completion.TrySetException(ex); }
        }));
        return completion.Task;
    }

    private Task<T> InvokeAsync<T>(Func<Task<T>> action, CancellationToken cancellationToken)
    {
        var dispatcher = _dispatcher ?? throw new InvalidOperationException("UI dispatcher 尚未初始化。");
        var completion = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        cancellationToken.Register(() => completion.TrySetCanceled(cancellationToken));
        dispatcher.BeginInvoke(new Action(async () =>
        {
            try { completion.TrySetResult(await action().ConfigureAwait(true)); }
            catch (Exception ex) { completion.TrySetException(ex); }
        }));
        return completion.Task;
    }

    private void Post(Action action)
    {
        var dispatcher = _dispatcher;
        if (dispatcher is null || dispatcher.IsDisposed)
        {
            return;
        }

        dispatcher.BeginInvoke(new Action(action));
    }
}
