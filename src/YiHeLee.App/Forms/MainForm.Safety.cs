namespace YiHeLee.App.Forms;

internal sealed partial class MainForm
{
    /// <summary>操作頁進度條下方的確認提示固定位置，由 BuildOperationsTab 建立。</summary>
    private Panel _safetyPromptHost = null!;

    private Control? _safetyPrompt;

    /// <summary>寫入 Excel 前的安全確認：固定內嵌在「操作」頁進度條下方，不另外跳出視窗、不蓋住整個畫面。</summary>
    public Task<bool> ConfirmExcelSafetyAsync(CancellationToken cancellationToken)
    {
        ShowAndActivate();
        ShowOperationsTab();

        var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var registration = cancellationToken.Register(() => tcs.TrySetCanceled(cancellationToken));

        RenderSafetyPrompt(confirmed =>
        {
            registration.Dispose();
            tcs.TrySetResult(confirmed);
        });

        return tcs.Task;
    }

    private void RenderSafetyPrompt(Action<bool> onResolved)
    {
        CloseSafetyPrompt();

        var group = new GroupBox
        {
            Text = "Excel 更新前確認",
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            Dock = DockStyle.Top,
            ForeColor = Color.DarkOrange,
            Padding = new Padding(14, 6, 14, 10),
            Margin = new Padding(0)
        };

        var content = new TableLayoutPanel
        {
            Dock = DockStyle.Top,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            ColumnCount = 1,
            RowCount = 2
        };
        content.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        content.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        content.RowStyles.Add(new RowStyle(SizeType.AutoSize));

        const string message =
            "系統即將讀取並更新 Excel。請先確認：\r\n" +
            "1. 指定活頁簿已用桌面版 Excel 開啟。\r\n" +
            "2. 已按 Enter 或 Esc，結束儲存格編輯。\r\n" +
            "3. 已關閉另存新檔、列印、尋找取代及其他 Excel 對話框。\r\n" +
            "4. 更新期間請勿關閉 Excel、另存新檔、執行巨集或改名／刪除輸出頁籤。\r\n\r\n" +
            "注意：程式完成時會儲存整份活頁簿，也會一併儲存您尚未儲存的變更。是否開始？";
        content.Controls.Add(new Label
        {
            Text = message,
            AutoSize = true,
            ForeColor = SystemColors.ControlText,
            Margin = new Padding(1, 2, 0, 10)
        }, 0, 0);

        var buttons = new FlowLayoutPanel
        {
            FlowDirection = FlowDirection.LeftToRight,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            WrapContents = false,
            Margin = new Padding(0)
        };
        var startButton = new Button { Text = "開始", AutoSize = true, AutoSizeMode = AutoSizeMode.GrowOnly, MinimumSize = new Size(120, 36), Padding = new Padding(12, 2, 12, 2), TextAlign = ContentAlignment.MiddleCenter, ForeColor = SystemColors.ControlText, Font = new Font(Font.FontFamily, 10F, FontStyle.Bold), Margin = new Padding(0, 0, 10, 0) };
        var cancelButton = new Button { Text = "取消", AutoSize = true, AutoSizeMode = AutoSizeMode.GrowOnly, MinimumSize = new Size(120, 36), Padding = new Padding(12, 2, 12, 2), TextAlign = ContentAlignment.MiddleCenter, ForeColor = SystemColors.ControlText, Margin = new Padding(0) };
        startButton.Click += (_, _) => Resolve(true);
        cancelButton.Click += (_, _) => Resolve(false);
        buttons.Controls.Add(startButton);
        buttons.Controls.Add(cancelButton);
        content.Controls.Add(buttons, 0, 1);

        group.Controls.Add(content);
        _safetyPromptHost.Controls.Add(group);
        AcceptButton = startButton;
        CancelButton = cancelButton;
        _safetyPrompt = group;
        startButton.Focus();

        void Resolve(bool confirmed)
        {
            CloseSafetyPrompt();
            onResolved(confirmed);
        }
    }

    private void CloseSafetyPrompt()
    {
        if (_safetyPrompt is null)
        {
            return;
        }

        AcceptButton = null;
        CancelButton = null;
        _safetyPromptHost.Controls.Remove(_safetyPrompt);
        _safetyPrompt.Dispose();
        _safetyPrompt = null;
    }
}
