using YiHeLee.Domain;

namespace YiHeLee.App.Forms;

internal sealed partial class MainForm
{
    private void RenderIdlePlaceholder()
    {
        ClearResultsTab();
        _resultsTab.Controls.Add(new Label
        {
            Dock = DockStyle.Fill,
            TextAlign = ContentAlignment.MiddleCenter,
            Text = "尚未有今日執行結果。點擊「操作」頁籤的「立即執行」，或等待每日台北時間 13:35 自動執行。",
            ForeColor = Color.Gray
        });
    }

    /// <summary>依最新一次執行結果重建「每日結果」頁籤內容，並自動切換過去。</summary>
    public void SwitchToResultsTab(JobRunSummary summary, bool isSuccess)
    {
        ClearResultsTab();
        if (isSuccess)
        {
            RenderSuccessContent(summary);
        }
        else
        {
            RenderFailureContent(summary);
        }

        _tabs.SelectedTab = _resultsTab;
    }

    private void ClearResultsTab()
    {
        var oldControls = _resultsTab.Controls.Cast<Control>().ToArray();
        _resultsTab.Controls.Clear();
        foreach (var control in oldControls)
        {
            control.Dispose();
        }
    }

    private void RenderSuccessContent(JobRunSummary summary)
    {
        var invalidCurrentPriceCount = summary.Alerts.Count(x => x.AlertKind == AlertKind.CurrentPriceInvalid);
        var invalidEntryAveragePriceCount = summary.Alerts.Count(x => x.AlertKind == AlertKind.EntryAveragePriceInvalid);
        var maAnomalies = summary.MovingAverageAnomalies ?? [];
        AddResultHeader(
            "Yi He Lee－每日均價判斷完成",
            $"資料日期：{summary.TargetDate:yyyy-MM-dd}　" +
            $"符合均價高於任一價格條件：{summary.AlertCount} 筆　" +
            $"均價資料異常：{maAnomalies.Count} 筆　" +
            $"進場價/平均價異常：{invalidEntryAveragePriceCount} 筆　" +
            $"現價異常：{invalidCurrentPriceCount} 筆　" +
            $"無法判斷：{summary.MissingIndicatorCount} 筆",
            Color.DarkGreen);
        AddResultFooter(summary.TargetDate, "開啟 Excel", _openExcelAction);

        var tabs = new TabControl { Dock = DockStyle.Fill, Padding = new Point(12, 6) };
        tabs.TabPages.Add(BuildMovingAverageAnomalyTab(maAnomalies));
        tabs.TabPages.Add(BuildTriggeredTab(summary.Alerts.Where(x => x.AlertKind == AlertKind.MovingAverageTriggered).ToArray()));
        tabs.TabPages.Add(BuildEntryAveragePriceInvalidTab(summary.Alerts.Where(x => x.AlertKind == AlertKind.EntryAveragePriceInvalid).ToArray()));
        tabs.TabPages.Add(BuildCurrentPriceInvalidTab(summary.Alerts.Where(x => x.AlertKind == AlertKind.CurrentPriceInvalid).ToArray()));
        tabs.TabPages.Add(BuildMissingTab(summary.Alerts.Where(x => x.AlertKind == AlertKind.TechnicalIndicatorMissing).ToArray()));
        _resultsTab.Controls.Add(tabs);
        tabs.BringToFront();
    }

    private void RenderFailureContent(JobRunSummary summary)
    {
        var retryDescription = summary.Outcome == RunOutcome.RetryableFailure
            ? "可立即重試，排程也會依設定再次執行。"
            : "請先依錯誤內容修正後，再按下立即重新執行。";
        AddResultHeader(
            "Yi He Lee－執行失敗",
            $"第 {summary.AttemptNumber} 次執行失敗。程式已記錄失敗狀態；{retryDescription}",
            Color.DarkRed);
        AddResultFooter(summary.TargetDate, "前往設定", () =>
        {
            ShowSettingsTab();
            return Task.CompletedTask;
        });

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
            Font = new Font(Font, FontStyle.Bold),
            Margin = new Padding(0, 0, 0, 12)
        }, 0, 0);
        panel.Controls.Add(new TextBox
        {
            Text = summary.Message,
            Dock = DockStyle.Fill,
            Multiline = true,
            ReadOnly = true,
            ScrollBars = ScrollBars.Vertical,
            BackColor = Color.White,
            Margin = new Padding(0, 0, 0, 12)
        }, 0, 1);
        var logButton = new Button { Text = "開啟 Log 資料夾", AutoSize = true, AutoSizeMode = AutoSizeMode.GrowOnly, MinimumSize = new Size(150, 36), Padding = new Padding(12, 2, 12, 2), TextAlign = ContentAlignment.MiddleCenter };
        logButton.Click += (_, _) => _openLogFolderAction();
        panel.Controls.Add(logButton, 0, 2);
        _resultsTab.Controls.Add(panel);
        panel.BringToFront();
    }

    private static TabPage BuildMovingAverageAnomalyTab(IReadOnlyList<DailyMovingAverageSnapshot> rows)
    {
        var tab = new TabPage($"均價資料異常（{rows.Count}）");
        if (rows.Count == 0)
        {
            tab.Controls.Add(new Label
            {
                Text = "今日均價前置資料沒有異常。",
                Dock = DockStyle.Fill,
                TextAlign = ContentAlignment.MiddleCenter,
                Font = new Font("Microsoft JhengHei UI", 13F, FontStyle.Bold),
                ForeColor = Color.DarkGreen
            });
            return tab;
        }

        var grid = CreateGrid();
        AddTextColumn(grid, "代碼", 85);
        AddTextColumn(grid, "名稱", 130);
        AddTextColumn(grid, "收盤價", 90);
        AddTextColumn(grid, "5日均價", 90);
        AddTextColumn(grid, "20日均價", 90);
        AddTextColumn(grid, "60日均價", 90);
        AddTextColumn(grid, "120日均價", 95);
        AddTextColumn(grid, "異常", 150);
        AddTextColumn(grid, "原因", 360, DataGridViewAutoSizeColumnMode.Fill);

        foreach (var row in rows)
        {
            var index = grid.Rows.Add(
                row.StockCode,
                row.StockName,
                FormatDecimal(row.ClosePrice),
                FormatMa(row.MovingAverage5),
                FormatMa(row.MovingAverage20),
                FormatMa(row.MovingAverage60),
                FormatMa(row.MovingAverage120),
                DescribeCalculationStatus(row.CalculationStatus),
                row.MissingReason ?? string.Empty);
            grid.Rows[index].DefaultCellStyle.BackColor = Color.LightYellow;
        }

        tab.Controls.Add(grid);
        return tab;
    }

    private void AddResultHeader(string title, string message, Color titleColor)
    {
        var header = new Panel { Dock = DockStyle.Top, Height = 89, Padding = new Padding(20, 16, 20, 10) };
        var titleLabel = new Label
        {
            Dock = DockStyle.Top,
            Height = 34,
            Text = title,
            Font = new Font("Microsoft JhengHei UI", 18F, FontStyle.Bold),
            ForeColor = titleColor
        };
        var messageLabel = new Label
        {
            Dock = DockStyle.Fill,
            Text = message,
            AutoEllipsis = true,
            ForeColor = Color.FromArgb(45, 45, 45)
        };
        var headerSeparator = new Panel { Dock = DockStyle.Bottom, Height = 1, BackColor = SystemColors.ControlDark };
        header.Controls.Add(messageLabel);
        header.Controls.Add(titleLabel);
        header.Controls.Add(headerSeparator);
        _resultsTab.Controls.Add(header);
    }

    private void AddResultFooter(DateOnly targetDate, string secondaryButtonText, Func<Task> secondaryAction)
    {
        var footer = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.RightToLeft,
            Padding = new Padding(16)
        };

        // 指定日期重跑屬於收盤／歷史資料流程（2026-07-13 盤中／收盤流程拆分），不啟動盤中客戶判斷。
        var retryButton = new Button { Text = "重新執行收盤更新", AutoSize = true, AutoSizeMode = AutoSizeMode.GrowOnly, MinimumSize = new Size(150, 36), Padding = new Padding(12, 2, 12, 2), TextAlign = ContentAlignment.MiddleCenter };
        retryButton.Click += async (_, _) => await ExecuteResultActionAsync(retryButton, () => _runCloseAction(targetDate));
        footer.Controls.Add(retryButton);

        var secondaryButton = new Button { Text = secondaryButtonText, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowOnly, MinimumSize = new Size(150, 36), Padding = new Padding(12, 2, 12, 2), TextAlign = ContentAlignment.MiddleCenter, Margin = new Padding(0, 0, 12, 0) };
        secondaryButton.Click += async (_, _) => await ExecuteResultActionAsync(secondaryButton, secondaryAction);
        footer.Controls.Add(secondaryButton);

        var footerContainer = new Panel { Dock = DockStyle.Bottom, Height = 66 };
        var footerSeparator = new Panel { Dock = DockStyle.Top, Height = 1, BackColor = SystemColors.ControlDark };
        footerContainer.Controls.Add(footer);
        footerContainer.Controls.Add(footerSeparator);

        _resultsTab.Controls.Add(footerContainer);
    }

    private async Task ExecuteResultActionAsync(Button button, Func<Task> action)
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

    /// <summary>
    /// 「符合均價條件」頁籤：「進場價/平均價」與「現價」是兩個完全獨立的欄位，必須分開顯示，
    /// 不可合併成單一「價格」欄位。每一條均價（MA5／MA20／MA120）只要大於或等於
    /// 「進場價/平均價」或「現價」其中一個價格就算成立，判斷明細欄位讓一般使用者不需理解程式邏輯即可看懂原因。
    /// </summary>
    private static TabPage BuildTriggeredTab(IReadOnlyList<StrategyAlert> alerts)
    {
        var tab = new TabPage($"符合均價條件（{alerts.Count}）");
        if (alerts.Count == 0)
        {
            tab.Controls.Add(new Label
            {
                Text = "今日沒有任何客戶股票符合 MA5、MA20 或 MA120 任一均價大於或等於「進場價/平均價」或「現價」。",
                Dock = DockStyle.Fill,
                TextAlign = ContentAlignment.MiddleCenter,
                Font = new Font("Microsoft JhengHei UI", 13F, FontStyle.Bold),
                ForeColor = Color.DarkGreen
            });
            return tab;
        }

        var grid = CreateGrid();
        AddTextColumn(grid, "頁籤", 130);
        AddTextColumn(grid, "代碼", 80);
        AddTextColumn(grid, "股票", 120);
        AddTextColumn(grid, "進場價/平均價", 110);
        AddTextColumn(grid, "現價", 90);
        AddTextColumn(grid, "收盤價", 85);
        AddTextColumn(grid, "5日均價", 85);
        AddTextColumn(grid, "20日均價", 85);
        AddTextColumn(grid, "120日均價", 90);
        AddTextColumn(grid, "符合均價", 150);
        var detailColumn = grid.Columns[grid.Columns.Add(new DataGridViewTextBoxColumn
        {
            HeaderText = "判斷明細",
            AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill,
            SortMode = DataGridViewColumnSortMode.NotSortable
        })];
        detailColumn.DefaultCellStyle.WrapMode = DataGridViewTriState.True;
        detailColumn.MinimumWidth = 320;

        foreach (var alert in alerts)
        {
            var index = grid.Rows.Add(
                alert.SheetName,
                alert.StockCode,
                alert.StockName,
                FormatEntryAveragePrice(alert.EntryAveragePrice),
                FormatCurrentPrice(alert.CurrentPrice),
                FormatDecimal(alert.ClosePrice),
                FormatMa(alert.MovingAverage5),
                FormatMa(alert.MovingAverage20),
                FormatMa(alert.MovingAverage120),
                BuildTriggerText(alert),
                BuildJudgmentDetail(alert));
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
        AddTextColumn(grid, "頁籤", 150);
        AddTextColumn(grid, "原始列", 70);
        AddTextColumn(grid, "代碼", 90);
        AddTextColumn(grid, "股票", 140);
        AddTextColumn(grid, "現價", 95);
        AddTextColumn(grid, "原因", 420, DataGridViewAutoSizeColumnMode.Fill);
        foreach (var alert in alerts)
        {
            var index = grid.Rows.Add(
                alert.SheetName,
                alert.ExcelRow,
                alert.StockCode,
                alert.StockName,
                FormatCurrentPrice(alert.CurrentPrice),
                alert.TriggerDescription);
            grid.Rows[index].DefaultCellStyle.BackColor = Color.LightYellow;
        }

        tab.Controls.Add(grid);
        return tab;
    }

    /// <summary>「現價」欄位串接外部 DDE，無法判讀（錯誤值、空白、0、文字）的持股集中在此頁籤告知使用者。
    /// 本頁籤只顯示 DDE 現價問題，不得讓使用者誤以為是進場成本（進場價/平均價）錯誤；
    /// 進場價/平均價異常請見「進場價／平均價異常」頁籤，兩者不得合併。</summary>
    private static TabPage BuildCurrentPriceInvalidTab(IReadOnlyList<StrategyAlert> alerts)
    {
        var tab = new TabPage($"現價異常（{alerts.Count}）");
        if (alerts.Count == 0)
        {
            tab.Controls.Add(new Label
            {
                Text = "所有持股的「現價」欄位（DDE）都能正常判讀。",
                Dock = DockStyle.Fill,
                TextAlign = ContentAlignment.MiddleCenter
            });
            return tab;
        }

        var grid = CreateGrid();
        AddTextColumn(grid, "頁籤", 150);
        AddTextColumn(grid, "原始列", 70);
        AddTextColumn(grid, "代碼", 90);
        AddTextColumn(grid, "股票", 140);
        AddTextColumn(grid, "現價（DDE）", 110);
        AddTextColumn(grid, "原因", 420, DataGridViewAutoSizeColumnMode.Fill);
        foreach (var alert in alerts)
        {
            var index = grid.Rows.Add(
                alert.SheetName,
                alert.ExcelRow,
                alert.StockCode,
                alert.StockName,
                FormatCurrentPrice(alert.CurrentPrice),
                alert.TriggerDescription);
            grid.Rows[index].DefaultCellStyle.BackColor = Color.MistyRose;
        }

        tab.Controls.Add(grid);
        return tab;
    }

    /// <summary>
    /// 「進場價/平均價」欄位（Excel 表頭「進場價/平均價」，非 DDE）無法判讀的持股集中在此頁籤告知使用者，
    /// 與「現價異常」（DDE）完全分開頁籤，不得混合顯示，避免使用者混淆兩種完全不同的欄位問題。
    /// </summary>
    private static TabPage BuildEntryAveragePriceInvalidTab(IReadOnlyList<StrategyAlert> alerts)
    {
        var tab = new TabPage($"進場價／平均價異常（{alerts.Count}）");
        if (alerts.Count == 0)
        {
            tab.Controls.Add(new Label
            {
                Text = "所有持股的「進場價/平均價」欄位都能正常判讀。",
                Dock = DockStyle.Fill,
                TextAlign = ContentAlignment.MiddleCenter
            });
            return tab;
        }

        var grid = CreateGrid();
        AddTextColumn(grid, "頁籤", 150);
        AddTextColumn(grid, "原始列", 70);
        AddTextColumn(grid, "代碼", 90);
        AddTextColumn(grid, "股票", 140);
        AddTextColumn(grid, "進場價/平均價", 120);
        AddTextColumn(grid, "原因", 400, DataGridViewAutoSizeColumnMode.Fill);
        foreach (var alert in alerts)
        {
            var index = grid.Rows.Add(
                alert.SheetName,
                alert.ExcelRow,
                alert.StockCode,
                alert.StockName,
                FormatEntryAveragePrice(alert.EntryAveragePrice),
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
        if (alert.TriggeredMa5) values.Add("MA5");
        if (alert.TriggeredMa20) values.Add("MA20");
        if (alert.TriggeredMa120) values.Add("MA120");
        return string.Join("、", values);
    }

    /// <summary>
    /// 「判斷明細」欄位：讓一般使用者不需理解程式邏輯即可看懂為何成立，例如：
    /// 「20日均價已 >= 進場價/平均價 470」。
    /// 只列出實際成立的價格條件；未成立的比較不顯示。同時符合多條均價或價格時以換行分隔，方便閱讀。
    /// internal 供 <c>MainFormResultsFormattingTests</c>（由 InternalsVisibleTo 開放給測試專案）驗證輸出文字。
    /// </summary>
    internal static string BuildJudgmentDetail(StrategyAlert alert)
    {
        var lines = new List<string>();
        AppendJudgmentLine(lines, "5日均價", alert.TriggeredMa5, alert.MovingAverage5, alert.EntryAveragePrice, alert.CurrentPrice);
        AppendJudgmentLine(lines, "20日均價", alert.TriggeredMa20, alert.MovingAverage20, alert.EntryAveragePrice, alert.CurrentPrice);
        AppendJudgmentLine(lines, "120日均價", alert.TriggeredMa120, alert.MovingAverage120, alert.EntryAveragePrice, alert.CurrentPrice);
        return lines.Count > 0 ? string.Join("\r\n", lines) : string.Empty;
    }

    private static void AppendJudgmentLine(
        List<string> lines,
        string label,
        bool triggered,
        decimal? movingAverage,
        decimal? entryAveragePrice,
        decimal? currentPrice)
    {
        if (!triggered || movingAverage is not decimal ma || entryAveragePrice is not decimal entry || currentPrice is not decimal current)
        {
            return;
        }

        if (ma >= entry)
        {
            lines.Add($"{label}已 >= 進場價/平均價 {entry:0.##}");
        }

        if (ma >= current)
        {
            lines.Add($"{label}已 >= 現價 {current:0.##}");
        }
    }

    private static string FormatDecimal(decimal? value) => value?.ToString("0.##") ?? string.Empty;

    // 現價來自外部 DDE，無法判讀時必須顯示文字，不得顯示空白或0，避免使用者誤以為現價就是0。
    internal static string FormatCurrentPrice(decimal? value) => value?.ToString("0.##") ?? "無效（DDE）";

    // 進場價/平均價不是 DDE 欄位，無法判讀時必須顯示文字（不得顯示空白或0），且不得寫成 DDE 異常，
    // 避免與「現價」欄位的錯誤原因混淆。
    internal static string FormatEntryAveragePrice(decimal? value) => value?.ToString("0.##") ?? "無效";

    // 均線資料不足時必須顯示文字，不得顯示空白或0，避免使用者誤判為尚未抓到資料以外的狀況。
    private static string FormatMa(decimal? value) => value?.ToString("0.##") ?? "尚未抓到資料";

    private static string DescribeCalculationStatus(CalculationStatus status) => status switch
    {
        CalculationStatus.Ok => "正常",
        CalculationStatus.TodayCloseMissing => "當日收盤價缺失",
        CalculationStatus.BackfillFailed => "歷史回補失敗",
        _ => "交易日數不足"
    };

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
