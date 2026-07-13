using YiHeLee.App.Infrastructure;
using YiHeLee.Domain;

namespace YiHeLee.App.Forms;

internal sealed partial class MainForm
{
    private Label _statusLabel = null!;
    private ProgressBar _statusProgressBar = null!;
    private Label _progressDetailLabel = null!;
    private Label _adminStatusLabel = null!;
    private Button _runIntradayButton = null!;
    private Button _runCloseButton = null!;
    private Button _historicalPriceButton = null!;
    private CheckBox _useManualRunDate = null!;
    private DateTimePicker _manualRunDate = null!;
    private Label _workflowStatusValueLabel = null!;

    /// <summary>詳細文字狀態是否顯示，由 AppSettings.ShowStatusText 這個 config 旗標控制，預設隱藏。</summary>
    private bool _statusTextEnabled;

    private TabPage BuildOperationsTab(bool showHistoricalPriceButton, bool showStatusText)
    {
        _statusTextEnabled = showStatusText;

        var tab = new TabPage("操作");
        var root = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 6,
            Padding = new Padding(20, 16, 20, 12)
        };
        root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));      // 執行區（日期＋按鈕）
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));      // 執行進度（執行中才顯示）
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));      // Excel 更新前確認（需要確認時才顯示）
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));      // 盤中監控／收盤更新狀態
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));  // 彈性空間
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));      // 權限狀態列（固定貼齊底部）

        // 防呆確認固定顯示在進度條正下方（見 MainForm.Safety.cs）。
        _safetyPromptHost = new Panel
        {
            Dock = DockStyle.Fill,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            Margin = new Padding(0)
        };

        root.Controls.Add(BuildRunGroup(showHistoricalPriceButton), 0, 0);
        root.Controls.Add(BuildProgressPanel(), 0, 1);
        root.Controls.Add(_safetyPromptHost, 0, 2);
        root.Controls.Add(BuildWorkflowStatusGroup(), 0, 3);
        root.Controls.Add(new Panel { Dock = DockStyle.Fill, Margin = new Padding(0) }, 0, 4);
        root.Controls.Add(BuildAdminStatusPanel(), 0, 5);

        tab.Controls.Add(root);
        return tab;
    }

    private Control BuildRunGroup(bool showHistoricalPriceButton)
    {
        var group = new GroupBox
        {
            Text = "執行",
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            Dock = DockStyle.Fill,
            Padding = new Padding(14, 6, 14, 10),
            Margin = new Padding(0, 0, 0, 14)
        };

        var layout = new FlowLayoutPanel
        {
            Dock = DockStyle.Top,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            FlowDirection = FlowDirection.TopDown,
            WrapContents = false
        };

        var manualDateRow = new FlowLayoutPanel
        {
            FlowDirection = FlowDirection.LeftToRight,
            AutoSize = true,
            WrapContents = false,
            Margin = new Padding(0, 2, 0, 10)
        };
        // 指定日期只適用於收盤更新（收盤／歷史資料流程）；盤中判斷一律使用今日與上一交易日基準。
        _useManualRunDate = new CheckBox
        {
            Text = "收盤更新指定日期（可回溯過去日期，未勾選則為今日；不適用盤中判斷）",
            AutoSize = true,
            Margin = new Padding(0, 3, 8, 0)
        };
        _manualRunDate = new DateTimePicker
        {
            Format = DateTimePickerFormat.Short,
            Enabled = false,
            MaxDate = DateTime.Today,
            Margin = new Padding(0, 1, 0, 0)
        };
        // 依實際字型量測需要的寬度（最寬日期＋下拉鈕＋邊框），文字放大時日期才不會被截斷。
        _manualRunDate.Width = TextRenderer.MeasureText("2026/12/31", Font).Width
            + SystemInformation.VerticalScrollBarWidth + 16;
        _useManualRunDate.CheckedChanged += (_, _) => _manualRunDate.Enabled = _useManualRunDate.Checked;
        manualDateRow.Controls.Add(_useManualRunDate);
        manualDateRow.Controls.Add(_manualRunDate);

        var buttons = new FlowLayoutPanel
        {
            FlowDirection = FlowDirection.LeftToRight,
            AutoSize = true,
            WrapContents = true,
            Margin = new Padding(0)
        };
        // 2026-07-13 盤中／收盤流程拆分：原「立即執行」語意不清楚，改為兩個明確操作。
        _runIntradayButton = CreateActionButton("立即執行盤中判斷", OnRunIntradayClick, primary: true);
        buttons.Controls.Add(_runIntradayButton);
        _runCloseButton = CreateActionButton("立即執行收盤更新", OnRunCloseClick, primary: true);
        buttons.Controls.Add(_runCloseButton);
        buttons.Controls.Add(CreateActionButton("開啟設定的 Excel", async (_, _) => await _openExcelAction()));
        _historicalPriceButton = CreateActionButton("歷史收盤價", (_, _) => _historicalPriceAction());
        _historicalPriceButton.Visible = showHistoricalPriceButton;
        buttons.Controls.Add(_historicalPriceButton);
        buttons.Controls.Add(CreateActionButton("開啟 Log 資料夾", (_, _) => _openLogFolderAction()));

        layout.Controls.Add(manualDateRow);
        layout.Controls.Add(buttons);
        group.Controls.Add(layout);
        return group;
    }

    private Control BuildProgressPanel()
    {
        var panel = new TableLayoutPanel
        {
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            ColumnCount = 1,
            RowCount = 3,
            Dock = DockStyle.Fill,
            Margin = new Padding(0)
        };
        panel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        panel.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        panel.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        panel.RowStyles.Add(new RowStyle(SizeType.AutoSize));

        // 詳細文字狀態會透露目前正在執行哪個步驟，是否顯示由 AppSettings.ShowStatusText 控制，預設隱藏。
        _statusLabel = new Label
        {
            AutoSize = true,
            Text = "狀態：初始化中",
            Margin = new Padding(1, 0, 0, 6),
            Visible = _statusTextEnabled
        };
        panel.Controls.Add(_statusLabel, 0, 0);

        // 進度條只在工作執行中（1～99%）顯示，閒置或完成時收起，不佔畫面。
        _statusProgressBar = new ProgressBar
        {
            Minimum = 0,
            Maximum = 100,
            Value = 0,
            Width = 480,
            Height = 18,
            Anchor = AnchorStyles.Left,
            Visible = false,
            Margin = new Padding(1, 0, 0, 6)
        };
        panel.Controls.Add(_statusProgressBar, 0, 1);

        // 長時間作業（例如 MA120 歷史回補）的逐日細節進度，一律顯示、不受 ShowStatusText 旗標影響；
        // 內容為空時整列收起。
        _progressDetailLabel = new Label
        {
            AutoSize = true,
            MaximumSize = new Size(900, 0),
            ForeColor = SystemColors.GrayText,
            Margin = new Padding(1, 0, 0, 6),
            Visible = false
        };
        panel.Controls.Add(_progressDetailLabel, 0, 2);
        return panel;
    }

    /// <summary>盤中監控／收盤更新狀態顯示（2026-07-13 盤中／收盤流程拆分新增）。</summary>
    private Control BuildWorkflowStatusGroup()
    {
        var group = new GroupBox
        {
            Text = "盤中監控／收盤更新狀態",
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            Dock = DockStyle.Fill,
            Padding = new Padding(14, 6, 14, 10),
            Margin = new Padding(0, 0, 0, 14)
        };

        _workflowStatusValueLabel = new Label
        {
            AutoSize = true,
            MaximumSize = new Size(900, 0),
            Margin = new Padding(1, 4, 0, 4),
            Text = "盤中監控：初始化中"
        };
        group.Controls.Add(_workflowStatusValueLabel);
        return group;
    }

    private Control BuildAdminStatusPanel()
    {
        var panel = new TableLayoutPanel
        {
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            ColumnCount = 1,
            RowCount = 2,
            Dock = DockStyle.Fill,
            Margin = new Padding(0)
        };
        panel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        panel.RowStyles.Add(new RowStyle(SizeType.Absolute, 1));
        panel.RowStyles.Add(new RowStyle(SizeType.AutoSize));

        var separator = new Panel { Dock = DockStyle.Fill, BackColor = SystemColors.ControlDark, Margin = new Padding(0) };
        panel.Controls.Add(separator, 0, 0);

        _adminStatusLabel = new Label
        {
            AutoSize = true,
            MaximumSize = new Size(900, 0),
            Margin = new Padding(1, 10, 0, 0)
        };
        UpdateAdministratorStatus();
        panel.Controls.Add(_adminStatusLabel, 0, 1);
        return panel;
    }

    private static Button CreateActionButton(string text, EventHandler onClick, bool primary = false)
    {
        var button = new Button
        {
            Text = text,
            // 文字放大（DPI／文字縮放）時自動加寬，避免按鈕文字被截斷。
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowOnly,
            MinimumSize = new Size(168, 36),
            Padding = new Padding(12, 2, 12, 2),
            TextAlign = ContentAlignment.MiddleCenter,
            Margin = new Padding(0, 0, 10, 0),
            Font = primary ? new Font("Microsoft JhengHei UI", 10F, FontStyle.Bold) : new Font("Microsoft JhengHei UI", 10F)
        };
        button.Click += onClick;
        return button;
    }

    /// <summary>「立即執行盤中判斷」：只使用上一交易日已保存均價，不受指定日期影響。</summary>
    private async void OnRunIntradayClick(object? sender, EventArgs e)
    {
        SetRunButtonsEnabled(false);
        // 按下執行立即帶出進度條；後續由盤中服務逐步回報「正在執行什麼」。
        _statusProgressBar.Value = 1;
        _statusProgressBar.Visible = true;
        _statusLabel.Text = "狀態：盤中判斷：開始執行";
        try
        {
            await _runIntradayAction();
        }
        finally
        {
            if (!IsDisposed)
            {
                SetRunButtonsEnabled(true);
            }
        }
    }

    /// <summary>「立即執行收盤更新」：官方收盤價與均價前置更新；指定過去日期仍屬收盤／歷史資料流程。</summary>
    private async void OnRunCloseClick(object? sender, EventArgs e)
    {
        SetRunButtonsEnabled(false);
        // 按下執行立即帶出進度條，之後由 UpdateStatus 依實際進度更新並在結束時收起。
        _statusProgressBar.Value = 1;
        _statusProgressBar.Visible = true;
        try
        {
            DateOnly? manualDate = _useManualRunDate.Checked
                ? DateOnly.FromDateTime(_manualRunDate.Value.Date)
                : null;
            await _runCloseAction(manualDate);
        }
        finally
        {
            if (!IsDisposed)
            {
                SetRunButtonsEnabled(true);
            }
        }
    }

    private void SetRunButtonsEnabled(bool enabled)
    {
        _runIntradayButton.Enabled = enabled;
        _runCloseButton.Enabled = enabled;
    }

    private void UpdateAdministratorStatus()
    {
        var isAdmin = AdministratorHelper.IsAdministrator();
        _adminStatusLabel.Text = isAdmin
            ? "警告：目前以系統管理員執行，Excel 也必須用系統管理員開啟，請改用一般權限重新啟動。"
            : "目前以一般權限執行（正確，需與一般方式開啟的 Excel 搭配）。";
        _adminStatusLabel.ForeColor = isAdmin ? Color.DarkRed : Color.DarkGreen;
    }

    /// <summary>供 TrayApplicationContext 同步目前執行狀態；percentComplete（0～100）驅動進度條，
    /// 進度條只在執行中（1～99%）顯示；message 只在 AppSettings.ShowStatusText 開啟時才顯示於文字狀態。</summary>
    public void UpdateStatus(string message, int percentComplete)
    {
        if (IsDisposed)
        {
            return;
        }

        _statusLabel.Text = "狀態：" + message;
        var value = Math.Clamp(percentComplete, 0, 100);
        _statusProgressBar.Value = value;
        var running = value > 0 && value < 100;
        _statusProgressBar.Visible = running;
        if (!running)
        {
            // 工作結束（完成或失敗）順帶清除細節進度，避免殘留。
            UpdateProgressDetail(string.Empty);
        }
    }

    /// <summary>顯示長時間作業的細節進度（例如 MA120 歷史回補逐日進度）；傳空字串清除並收起。</summary>
    public void UpdateProgressDetail(string message)
    {
        if (IsDisposed)
        {
            return;
        }

        _progressDetailLabel.Text = message;
        _progressDetailLabel.Visible = !string.IsNullOrWhiteSpace(message);
    }

    /// <summary>更新盤中監控／收盤更新狀態顯示（2026-07-13 新增）。畫面必須能看出
    /// 今日判斷日期與使用的均價基準日期是兩個不同日期。</summary>
    public void UpdateWorkflowStatus(MarketWorkflowStatusSnapshot snapshot)
    {
        if (IsDisposed)
        {
            return;
        }

        var monitorState = snapshot.Phase switch
        {
            MarketWorkflowPhase.IntradayMonitoring => "執行中",
            MarketWorkflowPhase.BaselineNotReady => "基準資料未就緒",
            MarketWorkflowPhase.NonTradingDay => "非交易時段（非交易日）",
            MarketWorkflowPhase.WaitingForClose => "已停止（等待收盤更新）",
            MarketWorkflowPhase.CloseCompleted => "已停止（今日收盤更新完成）",
            MarketWorkflowPhase.OutsideSchedule => "非交易時段",
            MarketWorkflowPhase.Disabled => "已停止（排程停用）",
            _ => "未知"
        };

        var lines = new[]
        {
            $"盤中監控：{monitorState}",
            $"今日判斷日期：{snapshot.EvaluationDate:yyyy-MM-dd}",
            $"目前均價基準日期：{FormatDate(snapshot.BaselineTradeDate)}",
            $"最後盤中判斷時間：{FormatTime(snapshot.LastIntradayEvaluatedAt)}",
            $"下一次盤中判斷時間：{FormatTime(snapshot.NextIntradayTickAt)}",
            $"最後收盤更新日期：{FormatDate(snapshot.LastCloseSucceededDate)}",
            $"下一次收盤更新時間：{FormatTime(snapshot.NextCloseRunAt)}",
            $"本次讀取持股數：{snapshot.HoldingCount}",
            $"目前成立條件數：{snapshot.ActiveTriggerCount}",
            $"新通知數：{snapshot.NewNotificationCount}",
            $"進場價/平均價異常數：{snapshot.EntryAveragePriceInvalidCount}",
            $"現價 DDE 異常數：{snapshot.CurrentPriceInvalidCount}"
        };
        _workflowStatusValueLabel.Text = string.Join(Environment.NewLine, lines);
    }

    private static string FormatDate(DateOnly? value) => value is DateOnly d ? d.ToString("yyyy-MM-dd") : "－";

    private static string FormatTime(DateTimeOffset? value) => value is DateTimeOffset t ? t.ToString("yyyy-MM-dd HH:mm:ss") : "－";

    /// <summary>設定儲存後即時同步「歷史收盤價」按鈕顯示狀態，不需重啟程式。</summary>
    private void UpdateHistoricalPriceButtonVisibility(bool visible) => _historicalPriceButton.Visible = visible;

    /// <summary>設定儲存後即時同步文字狀態顯示與否，不需重啟程式。</summary>
    private void UpdateStatusTextVisibility(bool visible)
    {
        _statusTextEnabled = visible;
        _statusLabel.Visible = visible;
    }
}
