using System.Diagnostics;
using YiHeLee.Domain;

namespace YiHeLee.App.Forms;

/// <summary>依需求顯示在螢幕正中央的成功、策略清單或失敗通知。</summary>
internal sealed class ResultCenterForm : Form
{
    private readonly Func<Task>? _retryAction;
    private readonly Func<Task>? _secondaryAction;
    private readonly Button? _retryButton;
    private readonly Button? _secondaryButton;

    private ResultCenterForm(
        string title,
        string message,
        Color titleColor,
        Func<Task>? retryAction,
        string? secondaryButtonText,
        Func<Task>? secondaryAction)
    {
        _retryAction = retryAction;
        _secondaryAction = secondaryAction;

        Text = title;
        StartPosition = FormStartPosition.CenterScreen;
        TopMost = true;
        ShowInTaskbar = true;
        MinimumSize = new Size(820, 520);
        Size = new Size(1180, 720);
        Font = new Font("Microsoft JhengHei UI", 10F);
        Icon = SystemIcons.Application;

        var header = new Panel { Dock = DockStyle.Top, Height = 88, Padding = new Padding(20, 16, 20, 10) };
        var titleLabel = new Label
        {
            Dock = DockStyle.Top,
            Height = 34,
            Text = title,
            Font = new Font(Font.FontFamily, 18F, FontStyle.Bold),
            ForeColor = titleColor
        };
        var messageLabel = new Label
        {
            Dock = DockStyle.Fill,
            Text = message,
            AutoEllipsis = true,
            ForeColor = Color.FromArgb(45, 45, 45)
        };
        header.Controls.Add(messageLabel);
        header.Controls.Add(titleLabel);
        Controls.Add(header);

        var footer = new FlowLayoutPanel
        {
            Dock = DockStyle.Bottom,
            Height = 64,
            FlowDirection = FlowDirection.RightToLeft,
            Padding = new Padding(12)
        };
        var closeButton = new Button { Text = "關閉", AutoSize = true, Padding = new Padding(18, 6, 18, 6) };
        closeButton.Click += (_, _) => Close();
        footer.Controls.Add(closeButton);

        if (_retryAction is not null)
        {
            _retryButton = new Button { Text = "立即重新執行", AutoSize = true, Padding = new Padding(18, 6, 18, 6) };
            _retryButton.Click += async (_, _) => await ExecuteActionAsync(_retryButton, _retryAction);
            footer.Controls.Add(_retryButton);
        }

        if (!string.IsNullOrWhiteSpace(secondaryButtonText) && _secondaryAction is not null)
        {
            _secondaryButton = new Button { Text = secondaryButtonText, AutoSize = true, Padding = new Padding(18, 6, 18, 6) };
            _secondaryButton.Click += async (_, _) => await ExecuteActionAsync(_secondaryButton, _secondaryAction);
            footer.Controls.Add(_secondaryButton);
        }

        Controls.Add(footer);
    }

    public static ResultCenterForm CreateSuccess(
        JobRunSummary summary,
        Func<Task> retryAction,
        Func<Task> openExcelAction)
    {
        var form = new ResultCenterForm(
            "Yi He Lee－每日均價判斷完成",
            $"資料日期：{summary.TargetDate:yyyy-MM-dd}　符合條件：{summary.AlertCount} 筆　無技術資料：{summary.MissingIndicatorCount} 筆",
            Color.DarkGreen,
            retryAction,
            "開啟 Excel",
            openExcelAction);

        var tabs = new TabControl { Dock = DockStyle.Fill, Padding = new Point(12, 6) };
        tabs.TabPages.Add(BuildTriggeredTab(summary.Alerts.Where(x => x.AlertKind == AlertKind.MovingAverageTriggered).ToArray()));
        tabs.TabPages.Add(BuildMissingTab(summary.Alerts.Where(x => x.AlertKind == AlertKind.TechnicalIndicatorMissing).ToArray()));
        form.Controls.Add(tabs);
        tabs.BringToFront();
        return form;
    }

    public static ResultCenterForm CreateFailure(
        JobRunSummary summary,
        Func<Task> retryAction,
        Func<Task> settingsAction,
        Func<Task> logFolderAction)
    {
        var retryDescription = summary.Outcome == RunOutcome.RetryableFailure
            ? "可立即重試，排程也會依設定再次執行。"
            : "請先依錯誤內容修正後，再按下立即重新執行。";
        var form = new ResultCenterForm(
            "Yi He Lee－執行失敗",
            $"第 {summary.AttemptNumber} 次執行失敗。程式已記錄失敗狀態；{retryDescription}",
            Color.DarkRed,
            retryAction,
            "開啟設定",
            settingsAction);

        var panel = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 3,
            Padding = new Padding(24)
        };
        panel.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        panel.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        panel.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        panel.Controls.Add(new Label
        {
            Text = $"狀態：{ToJobStatusText(summary.Status)}\r\n結果：{ToOutcomeText(summary.Outcome)}\r\n目標日期：{summary.TargetDate:yyyy-MM-dd}",
            AutoSize = true,
            ForeColor = Color.DarkRed,
            Font = new Font(form.Font, FontStyle.Bold)
        }, 0, 0);
        panel.Controls.Add(new TextBox
        {
            Text = summary.Message,
            Dock = DockStyle.Fill,
            Multiline = true,
            ReadOnly = true,
            ScrollBars = ScrollBars.Vertical,
            BackColor = Color.White
        }, 0, 1);
        var logButton = new Button { Text = "開啟 Log 資料夾", AutoSize = true, Padding = new Padding(14, 5, 14, 5) };
        logButton.Click += async (_, _) => await logFolderAction();
        panel.Controls.Add(logButton, 0, 2);
        form.Controls.Add(panel);
        panel.BringToFront();
        return form;
    }

    protected override void OnShown(EventArgs e)
    {
        base.OnShown(e);
        CenterToScreen();
        BringToFront();
        Activate();
    }

    private static TabPage BuildTriggeredTab(IReadOnlyList<StrategyAlert> alerts)
    {
        var tab = new TabPage($"符合均價條件（{alerts.Count}）");
        if (alerts.Count == 0)
        {
            tab.Controls.Add(new Label
            {
                Text = "今日沒有任何客戶股票符合 5 日、20 日或 120 日均價 <= 進場價／平均價。",
                Dock = DockStyle.Fill,
                TextAlign = ContentAlignment.MiddleCenter,
                Font = new Font("Microsoft JhengHei UI", 13F, FontStyle.Bold),
                ForeColor = Color.DarkGreen
            });
            return tab;
        }

        var grid = CreateGrid();
        AddTextColumn(grid, "客戶", 120);
        AddTextColumn(grid, "頁籤", 140);
        AddTextColumn(grid, "代碼", 85);
        AddTextColumn(grid, "股票", 130);
        AddTextColumn(grid, "進場價/平均價", 115);
        AddTextColumn(grid, "收盤價", 90);
        AddTextColumn(grid, "5日均價", 90);
        AddTextColumn(grid, "20日均價", 90);
        AddTextColumn(grid, "120日均價", 95);
        AddTextColumn(grid, "觸發條件", 260, DataGridViewAutoSizeColumnMode.Fill);

        foreach (var alert in alerts)
        {
            var index = grid.Rows.Add(
                alert.CustomerName,
                alert.SheetName,
                alert.StockCode,
                alert.StockName,
                FormatDecimal(alert.EntryAveragePrice),
                FormatDecimal(alert.ClosePrice),
                FormatDecimal(alert.MovingAverage5),
                FormatDecimal(alert.MovingAverage20),
                FormatDecimal(alert.MovingAverage120),
                BuildTriggerText(alert));
            grid.Rows[index].DefaultCellStyle.BackColor = index % 2 == 0 ? Color.White : Color.FromArgb(242, 248, 252);
        }

        tab.Controls.Add(grid);
        return tab;
    }

    private static TabPage BuildMissingTab(IReadOnlyList<StrategyAlert> alerts)
    {
        var tab = new TabPage($"無法判斷（{alerts.Count}）");
        if (alerts.Count == 0)
        {
            tab.Controls.Add(new Label
            {
                Text = "所有有效持股都能在本次多頭／空頭完整清單中找到技術資料。",
                Dock = DockStyle.Fill,
                TextAlign = ContentAlignment.MiddleCenter
            });
            return tab;
        }

        var grid = CreateGrid();
        AddTextColumn(grid, "客戶", 120);
        AddTextColumn(grid, "頁籤", 150);
        AddTextColumn(grid, "原始列", 70);
        AddTextColumn(grid, "代碼", 90);
        AddTextColumn(grid, "股票", 140);
        AddTextColumn(grid, "進場價/平均價", 120);
        AddTextColumn(grid, "原因", 420, DataGridViewAutoSizeColumnMode.Fill);
        foreach (var alert in alerts)
        {
            var index = grid.Rows.Add(
                alert.CustomerName,
                alert.SheetName,
                alert.ExcelRow,
                alert.StockCode,
                alert.StockName,
                FormatDecimal(alert.EntryAveragePrice),
                alert.TriggerDescription);
            grid.Rows[index].DefaultCellStyle.BackColor = Color.LightYellow;
        }

        tab.Controls.Add(grid);
        return tab;
    }

    private static DataGridView CreateGrid() => new()
    {
        Dock = DockStyle.Fill,
        ReadOnly = true,
        AllowUserToAddRows = false,
        AllowUserToDeleteRows = false,
        AllowUserToResizeRows = false,
        RowHeadersVisible = false,
        SelectionMode = DataGridViewSelectionMode.FullRowSelect,
        MultiSelect = false,
        AutoSizeRowsMode = DataGridViewAutoSizeRowsMode.AllCells,
        BackgroundColor = Color.White,
        BorderStyle = BorderStyle.Fixed3D
    };

    private static void AddTextColumn(
        DataGridView grid,
        string header,
        int width,
        DataGridViewAutoSizeColumnMode autoSizeMode = DataGridViewAutoSizeColumnMode.None)
    {
        grid.Columns.Add(new DataGridViewTextBoxColumn
        {
            HeaderText = header,
            Width = width,
            AutoSizeMode = autoSizeMode,
            SortMode = DataGridViewColumnSortMode.Automatic
        });
    }

    private static string BuildTriggerText(StrategyAlert alert)
    {
        var values = new List<string>();
        if (alert.TriggeredMa5) values.Add("5 日均價");
        if (alert.TriggeredMa20) values.Add("20 日均價");
        if (alert.TriggeredMa120) values.Add("120 日均價");
        return string.Join("、", values) + " <= 進場價/平均價";
    }

    private static string FormatDecimal(decimal? value) => value?.ToString("0.##") ?? string.Empty;

    private async Task ExecuteActionAsync(Button button, Func<Task> action)
    {
        button.Enabled = false;
        try
        {
            await action();
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "操作失敗", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
        finally
        {
            if (!IsDisposed)
            {
                button.Enabled = true;
            }
        }
    }

    private static string ToJobStatusText(JobStatus status) => status switch
    {
        JobStatus.WebsiteNotUpdated => "網站尚未更新當日資料",
        JobStatus.CrawlFailed => "爬取失敗",
        JobStatus.ValidationFailed => "設定或資料驗證失敗",
        JobStatus.ExcelUnavailable => "Excel 無法使用",
        JobStatus.ExcelWriteFailed => "Excel 寫入失敗",
        JobStatus.Cancelled => "已取消",
        _ => status.ToString()
    };

    private static string ToOutcomeText(RunOutcome outcome) => outcome switch
    {
        RunOutcome.RetryableFailure => "可重試失敗",
        RunOutcome.NonRetryableFailure => "需人工修正後再執行",
        _ => outcome.ToString()
    };
}
