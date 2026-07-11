using System.ComponentModel;
using YiHeLee.Application.Services;
using YiHeLee.Domain;

namespace YiHeLee.App.Forms;

internal sealed partial class MainForm
{
    private readonly TextBox _workbookPathTextBox = new();
    private readonly TextBox _outputWorksheetTextBox = new();
    private readonly NumericUpDown _retryMinutes = new();
    private readonly NumericUpDown _maximumAttempts = new();
    private readonly NumericUpDown _crawlerRetryCount = new();
    private readonly NumericUpDown _crawlerRetryDelay = new();
    private readonly NumericUpDown _excelRetryCount = new();
    private readonly NumericUpDown _excelRetryDelay = new();
    private readonly CheckBox _startWithWindows = new();
    private readonly CheckBox _startMinimized = new();
    private readonly CheckBox _backupBeforeWrite = new();
    private readonly CheckBox _createOutputSheet = new();
    private readonly CheckBox _showSafetyPrompt = new();
    private readonly CheckBox _autoOpenWorkbook = new();
    private readonly CheckBox _enableDailySchedule = new();
    private readonly TextBox _excludedColors = new();
    private readonly TextBox _excludedMarkers = new();
    private readonly BindingList<SourceRowModel> _sourceRows = [];
    private readonly DataGridView _sourcesGrid = new();
    private Button _saveSettingsButton = null!;

    /// <summary>「歷史收盤價」按鈕顯示與否僅為 config 旗標，故意不放進設定頁籤 UI，
    /// 這裡只是儲存時原樣保留，避免每次儲存設定都被重置回預設值。</summary>
    private bool _showHistoricalPriceButtonSetting;

    /// <summary>文字狀態顯示與否僅為 config 旗標，故意不放進設定頁籤 UI，
    /// 這裡只是儲存時原樣保留，避免每次儲存設定都被重置回預設值。</summary>
    private bool _showStatusTextSetting;

    /// <summary>「資料來源網址」頁籤顯示與否僅為 config 旗標（ShowSourceSettings），故意不放進設定頁籤 UI，
    /// 這裡只是儲存時原樣保留。隱藏時來源清單仍會原樣載入並隨設定一起儲存，不會遺失。</summary>
    private bool _showSourceSettingsSetting;

    private TabPage BuildSettingsTab(AppSettings settings)
    {
        var tab = new TabPage("設定");

        var innerTabs = new TabControl { Dock = DockStyle.Fill };
        innerTabs.TabPages.Add(BuildGeneralSettingsTab());
        if (settings.ShowSourceSettings)
        {
            innerTabs.TabPages.Add(BuildSourcesTab());
        }

        _saveSettingsButton = new Button
        {
            Text = "儲存設定",
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowOnly,
            MinimumSize = new Size(140, 38),
            Padding = new Padding(12, 2, 12, 2),
            TextAlign = ContentAlignment.MiddleCenter,
            Font = new Font("Microsoft JhengHei UI", 10F, FontStyle.Bold)
        };
        _saveSettingsButton.Click += OnSaveSettingsClick;
        var footer = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.RightToLeft,
            AutoSize = true,
            Padding = new Padding(12)
        };
        footer.Controls.Add(_saveSettingsButton);

        var footerContainer = new Panel { Dock = DockStyle.Fill, AutoSize = true };
        var footerSeparator = new Panel { Dock = DockStyle.Top, Height = 1, BackColor = SystemColors.ControlDark };
        footerContainer.Controls.Add(footer);
        footerContainer.Controls.Add(footerSeparator);

        var root = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 2 };
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.Controls.Add(innerTabs, 0, 0);
        root.Controls.Add(footerContainer, 0, 1);
        tab.Controls.Add(root);

        LoadSettingsIntoFields(settings);
        return tab;
    }

    private TabPage BuildGeneralSettingsTab()
    {
        var tab = new TabPage("一般設定");

        // 排版：Excel 設定與重試設定（標籤較長）各佔一整列完整寬度；
        // 行為選項與排除規則高度相近，左右並排以減少捲動與留白。
        var layout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            AutoScroll = true,
            ColumnCount = 2,
            RowCount = 3,
            Padding = new Padding(16, 14, 16, 6)
        };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));

        var excelGroup = BuildExcelSettingsGroup();
        layout.Controls.Add(excelGroup, 0, 0);
        layout.SetColumnSpan(excelGroup, 2);

        var retryGroup = BuildScheduleAndRetryGroup();
        layout.Controls.Add(retryGroup, 0, 1);
        layout.SetColumnSpan(retryGroup, 2);

        var behaviorGroup = BuildBehaviorOptionsGroup();
        behaviorGroup.Margin = new Padding(0, 0, 7, 8);
        layout.Controls.Add(behaviorGroup, 0, 2);

        var exclusionGroup = BuildExclusionRulesGroup();
        exclusionGroup.Margin = new Padding(7, 0, 0, 8);
        layout.Controls.Add(exclusionGroup, 1, 2);

        tab.Controls.Add(layout);
        return tab;
    }

    private Control BuildExcelSettingsGroup()
    {
        var group = CreateSectionGroup("Excel 設定");
        var panel = CreateFieldPanel();

        var row = 0;
        AddLabel(panel, row, "Excel 檔案");
        _workbookPathTextBox.Dock = DockStyle.Fill;
        panel.Controls.Add(_workbookPathTextBox, 1, row);
        var browseButton = new Button { Text = "選擇檔案…", AutoSize = true };
        browseButton.Click += (_, _) => BrowseWorkbook();
        panel.Controls.Add(browseButton, 2, row++);

        AddLabel(panel, row, "輸出頁籤");
        _outputWorksheetTextBox.Dock = DockStyle.Fill;
        panel.Controls.Add(_outputWorksheetTextBox, 1, row);
        panel.SetColumnSpan(_outputWorksheetTextBox, 2);
        row++;

        AddLabel(panel, row, "每日固定執行時間");
        panel.Controls.Add(new Label
        {
            Text = "Asia/Taipei 13:35（固定，不可修改）",
            AutoSize = true,
            ForeColor = Color.DarkBlue,
            Anchor = AnchorStyles.Left
        }, 1, row);
        row++;

        group.Controls.Add(panel);
        return group;
    }

    private Control BuildScheduleAndRetryGroup()
    {
        var group = CreateSectionGroup("執行次數與重試設定");

        // 六個數值欄位改為每列兩組（次數｜等待），高度減半、相關欄位並列好對照。
        var panel = new TableLayoutPanel
        {
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            ColumnCount = 4,
            Dock = DockStyle.Top
        };
        panel.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        panel.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        panel.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        panel.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));

        ConfigureNumeric(_retryMinutes, 1, 240);
        ConfigureNumeric(_maximumAttempts, 1, 100);
        ConfigureNumeric(_crawlerRetryCount, 1, 20);
        ConfigureNumeric(_crawlerRetryDelay, 1, 120);
        ConfigureNumeric(_excelRetryCount, 1, 20);
        ConfigureNumeric(_excelRetryDelay, 1, 120);

        AddNumericPairRow(panel, 0, "網站／Excel 長時間重試間隔（分鐘）", _retryMinutes, "每日最大執行次數", _maximumAttempts);
        AddNumericPairRow(panel, 1, "每次爬蟲短暫重試次數", _crawlerRetryCount, "爬蟲短暫重試等待（秒）", _crawlerRetryDelay);
        AddNumericPairRow(panel, 2, "Excel 忙碌短暫重試次數", _excelRetryCount, "Excel 忙碌重試等待（秒）", _excelRetryDelay);

        group.Controls.Add(panel);
        return group;
    }

    private static void AddNumericPairRow(
        TableLayoutPanel panel,
        int row,
        string leftLabel,
        NumericUpDown leftNumeric,
        string rightLabel,
        NumericUpDown rightNumeric)
    {
        panel.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        panel.Controls.Add(CreateFieldLabel(leftLabel), 0, row);
        leftNumeric.Width = 110;
        leftNumeric.Anchor = AnchorStyles.Left;
        leftNumeric.Margin = new Padding(0, 3, 28, 3);
        panel.Controls.Add(leftNumeric, 1, row);
        panel.Controls.Add(CreateFieldLabel(rightLabel), 2, row);
        rightNumeric.Width = 110;
        rightNumeric.Anchor = AnchorStyles.Left;
        rightNumeric.Margin = new Padding(0, 3, 0, 3);
        panel.Controls.Add(rightNumeric, 3, row);
    }

    private Control BuildBehaviorOptionsGroup()
    {
        var group = CreateSectionGroup("啟動與行為選項");
        var panel = new FlowLayoutPanel
        {
            Dock = DockStyle.Top,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            FlowDirection = FlowDirection.TopDown,
            WrapContents = false
        };

        (CheckBox CheckBox, string Text)[] options =
        [
            (_startWithWindows, "登入 Windows 後自動啟動"),
            (_startMinimized, "啟動後只顯示在右下角系統匣"),
            (_backupBeforeWrite, "寫入 Excel 前建立備份"),
            (_createOutputSheet, "找不到輸出頁籤時自動建立"),
            (_showSafetyPrompt, "操作 Excel 前顯示防呆確認"),
            (_autoOpenWorkbook, "找不到已開啟的活頁簿時自動開啟 Excel 檔案"),
            (_enableDailySchedule, "每日 13:35 自動執行排程")
        ];
        foreach (var (checkBox, text) in options)
        {
            checkBox.Text = text;
            checkBox.AutoSize = true;
            checkBox.Margin = new Padding(2, 6, 0, 6);
            panel.Controls.Add(checkBox);
        }

        group.Controls.Add(panel);
        return group;
    }

    private Control BuildExclusionRulesGroup()
    {
        var group = CreateSectionGroup("持股判斷排除規則");
        var panel = CreateFieldPanel();

        var row = 0;
        AddLabel(panel, row, "不判斷的股名填滿色");
        _excludedColors.Dock = DockStyle.Fill;
        _excludedColors.PlaceholderText = "例如：#92D050，多個色碼請用逗號分隔";
        panel.Controls.Add(_excludedColors, 1, row);
        panel.SetColumnSpan(_excludedColors, 2);
        row++;

        AddLabel(panel, row, "不判斷的文字標記");
        _excludedMarkers.Dock = DockStyle.Fill;
        _excludedMarkers.Multiline = true;
        _excludedMarkers.Height = 70;
        _excludedMarkers.PlaceholderText = "例如：不判斷、已出場；多個文字請用逗號或換行分隔";
        panel.Controls.Add(_excludedMarkers, 1, row);
        panel.SetColumnSpan(_excludedMarkers, 2);
        row++;

        group.Controls.Add(panel);
        return group;
    }

    // GroupBox 不可同時設定 AutoSize 與固定 Width（會被縮到最小寬度導致標題直向破版）；
    // 改用 Dock.Fill 讓寬度跟隨 TableLayoutPanel 欄寬，高度由 AutoSize 列自動計算。
    private static GroupBox CreateSectionGroup(string title) => new()
    {
        Text = title,
        AutoSize = true,
        AutoSizeMode = AutoSizeMode.GrowAndShrink,
        Dock = DockStyle.Fill,
        Padding = new Padding(14, 6, 14, 10),
        Margin = new Padding(0, 0, 0, 14)
    };

    private static TableLayoutPanel CreateFieldPanel()
    {
        var panel = new TableLayoutPanel
        {
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            ColumnCount = 3,
            Dock = DockStyle.Top
        };
        // 標籤欄用 AutoSize 而非固定寬度，避免不同 DPI 下長標籤被折行。
        panel.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        panel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        panel.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        return panel;
    }

    private TabPage BuildSourcesTab()
    {
        var tab = new TabPage("資料來源網址") { Padding = new Padding(12) };
        var note = new Label
        {
            Dock = DockStyle.Top,
            Height = 48,
            Padding = new Padding(4, 4, 4, 12),
            Text = "固定兩個鉅亨網來源不可停用或刪除。可新增 N 個網址；不同網站若 HTML 結構不同，必須另外開發對應的 ProviderKey 爬蟲。",
            ForeColor = Color.DarkBlue
        };

        _sourcesGrid.Dock = DockStyle.Fill;
        _sourcesGrid.Margin = new Padding(0, 8, 0, 8);
        _sourcesGrid.BorderStyle = BorderStyle.FixedSingle;
        _sourcesGrid.AutoGenerateColumns = false;
        _sourcesGrid.AllowUserToAddRows = false;
        _sourcesGrid.AllowUserToDeleteRows = false;
        _sourcesGrid.SelectionMode = DataGridViewSelectionMode.FullRowSelect;
        _sourcesGrid.MultiSelect = false;
        _sourcesGrid.RowHeadersVisible = false;
        _sourcesGrid.AutoSizeRowsMode = DataGridViewAutoSizeRowsMode.AllCells;
        _sourcesGrid.DataError += (_, _) => { };
        _sourcesGrid.CellBeginEdit += (_, e) =>
        {
            if (e.RowIndex >= 0 && _sourceRows[e.RowIndex].Required)
            {
                e.Cancel = true;
            }
        };

        _sourcesGrid.Columns.Add(new DataGridViewCheckBoxColumn
        {
            HeaderText = "啟用",
            DataPropertyName = nameof(SourceRowModel.Enabled),
            Width = 58
        });
        _sourcesGrid.Columns.Add(new DataGridViewTextBoxColumn
        {
            HeaderText = "顯示名稱",
            DataPropertyName = nameof(SourceRowModel.DisplayName),
            Width = 180
        });
        _sourcesGrid.Columns.Add(new DataGridViewTextBoxColumn
        {
            HeaderText = "網址",
            DataPropertyName = nameof(SourceRowModel.Url),
            AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill,
            MinimumWidth = 300
        });
        _sourcesGrid.Columns.Add(new DataGridViewComboBoxColumn
        {
            HeaderText = "資料類型",
            DataPropertyName = nameof(SourceRowModel.IndicatorType),
            DataSource = Enum.GetValues<IndicatorType>(),
            Width = 150
        });
        _sourcesGrid.Columns.Add(new DataGridViewTextBoxColumn
        {
            HeaderText = "ProviderKey",
            DataPropertyName = nameof(SourceRowModel.ProviderKey),
            Width = 190
        });
        _sourcesGrid.Columns.Add(new DataGridViewCheckBoxColumn
        {
            HeaderText = "固定",
            DataPropertyName = nameof(SourceRowModel.Required),
            ReadOnly = true,
            Width = 58
        });
        _sourcesGrid.DataSource = _sourceRows;

        var addButton = new Button { Text = "新增網址", AutoSize = true, Padding = new Padding(14, 5, 14, 5), Margin = new Padding(0, 8, 10, 0) };
        addButton.Click += (_, _) =>
        {
            _sourceRows.Add(new SourceRowModel
            {
                SourceKey = $"CUSTOM_{Guid.NewGuid():N}".ToUpperInvariant(),
                DisplayName = "自訂來源",
                Url = "https://",
                IndicatorType = IndicatorType.BullishAlignment,
                ProviderKey = "CnyesTechnicalAlignment",
                Enabled = true,
                Required = false
            });
        };
        var removeButton = new Button { Text = "刪除選取網址", AutoSize = true, Padding = new Padding(14, 5, 14, 5), Margin = new Padding(0, 8, 0, 0) };
        removeButton.Click += (_, _) => RemoveSelectedSource();

        var buttons = new FlowLayoutPanel
        {
            Dock = DockStyle.Bottom,
            AutoSize = true,
            Padding = new Padding(0, 4, 0, 0)
        };
        buttons.Controls.Add(addButton);
        buttons.Controls.Add(removeButton);

        tab.Controls.Add(_sourcesGrid);
        tab.Controls.Add(buttons);
        tab.Controls.Add(note);
        return tab;
    }

    private void LoadSettingsIntoFields(AppSettings settings)
    {
        _workbookPathTextBox.Text = settings.WorkbookPath;
        _outputWorksheetTextBox.Text = settings.OutputWorksheetName;
        _retryMinutes.Value = Clamp(settings.RetryIntervalMinutes, _retryMinutes);
        _maximumAttempts.Value = Clamp(settings.MaximumDailyAttempts, _maximumAttempts);
        _crawlerRetryCount.Value = Clamp(settings.CrawlerShortRetryCount, _crawlerRetryCount);
        _crawlerRetryDelay.Value = Clamp(settings.CrawlerShortRetryDelaySeconds, _crawlerRetryDelay);
        _excelRetryCount.Value = Clamp(settings.ExcelShortRetryCount, _excelRetryCount);
        _excelRetryDelay.Value = Clamp(settings.ExcelShortRetryDelaySeconds, _excelRetryDelay);
        _startWithWindows.Checked = settings.StartWithWindows;
        _startMinimized.Checked = settings.StartMinimized;
        _backupBeforeWrite.Checked = settings.RequireBackupBeforeExcelWrite;
        _createOutputSheet.Checked = settings.CreateOutputWorksheetIfMissing;
        _showSafetyPrompt.Checked = settings.ShowExcelSafetyPrompt;
        _autoOpenWorkbook.Checked = settings.AutoOpenWorkbookIfClosed;
        _enableDailySchedule.Checked = settings.EnableDailySchedule;
        _excludedColors.Text = string.Join(", ", settings.ExcludedHoldingFillColors);
        _excludedMarkers.Text = string.Join(Environment.NewLine, settings.ExcludedHoldingTextMarkers);
        _showHistoricalPriceButtonSetting = settings.ShowHistoricalPriceButton;
        _showStatusTextSetting = settings.ShowStatusText;
        _showSourceSettingsSetting = settings.ShowSourceSettings;

        foreach (var source in settings.Sources)
        {
            _sourceRows.Add(SourceRowModel.From(source));
        }
    }

    private void BrowseWorkbook()
    {
        using var dialog = new OpenFileDialog
        {
            Title = "選擇親帶績效 Excel",
            Filter = "Excel 活頁簿 (*.xlsx;*.xlsm)|*.xlsx;*.xlsm|所有檔案 (*.*)|*.*",
            CheckFileExists = true,
            Multiselect = false
        };
        if (File.Exists(_workbookPathTextBox.Text))
        {
            dialog.FileName = _workbookPathTextBox.Text;
        }

        if (dialog.ShowDialog(this) == DialogResult.OK)
        {
            _workbookPathTextBox.Text = dialog.FileName;
        }
    }

    private void RemoveSelectedSource()
    {
        if (_sourcesGrid.CurrentRow?.DataBoundItem is not SourceRowModel row)
        {
            return;
        }

        if (row.Required)
        {
            MessageBox.Show(this, "固定來源不可刪除。", "Yi He Lee", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        _sourceRows.Remove(row);
    }

    private async void OnSaveSettingsClick(object? sender, EventArgs e)
    {
        _sourcesGrid.EndEdit();
        var settings = BuildSettingsFromFields();

        _validationService.EnsureFixedSources(settings);
        var errors = _validationService.Validate(settings);
        if (errors.Count > 0)
        {
            MessageBox.Show(this, string.Join(Environment.NewLine, errors), "設定有誤",
                MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        _saveSettingsButton.Enabled = false;
        try
        {
            await _onSaveSettings(settings);
            UpdateAdministratorStatus();
            UpdateHistoricalPriceButtonVisibility(settings.ShowHistoricalPriceButton);
            UpdateStatusTextVisibility(settings.ShowStatusText);
            MessageBox.Show(this, "設定已儲存。", "Yi He Lee", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "Yi He Lee－設定失敗", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
        finally
        {
            if (!IsDisposed)
            {
                _saveSettingsButton.Enabled = true;
            }
        }
    }

    private AppSettings BuildSettingsFromFields() => new()
    {
        WorkbookPath = _workbookPathTextBox.Text.Trim(),
        OutputWorksheetName = _outputWorksheetTextBox.Text.Trim(),
        DailyRunTime = AppSettings.FixedDailyRunTime,
        RetryIntervalMinutes = Decimal.ToInt32(_retryMinutes.Value),
        MaximumDailyAttempts = Decimal.ToInt32(_maximumAttempts.Value),
        CrawlerShortRetryCount = Decimal.ToInt32(_crawlerRetryCount.Value),
        CrawlerShortRetryDelaySeconds = Decimal.ToInt32(_crawlerRetryDelay.Value),
        ExcelShortRetryCount = Decimal.ToInt32(_excelRetryCount.Value),
        ExcelShortRetryDelaySeconds = Decimal.ToInt32(_excelRetryDelay.Value),
        StartWithWindows = _startWithWindows.Checked,
        StartMinimized = _startMinimized.Checked,
        RequireBackupBeforeExcelWrite = _backupBeforeWrite.Checked,
        CreateOutputWorksheetIfMissing = _createOutputSheet.Checked,
        ShowExcelSafetyPrompt = _showSafetyPrompt.Checked,
        AutoOpenWorkbookIfClosed = _autoOpenWorkbook.Checked,
        EnableDailySchedule = _enableDailySchedule.Checked,
        ShowHistoricalPriceButton = _showHistoricalPriceButtonSetting,
        ShowStatusText = _showStatusTextSetting,
        ShowSourceSettings = _showSourceSettingsSetting,
        ExcludedHoldingFillColors = SplitValues(_excludedColors.Text),
        ExcludedHoldingTextMarkers = SplitValues(_excludedMarkers.Text),
        Sources = _sourceRows.Select(x => x.ToSetting()).ToList()
    };

    private static List<string> SplitValues(string value) => value
        .Split([',', ';', '，', '；', '\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
        .Distinct(StringComparer.OrdinalIgnoreCase)
        .ToList();

    private static void AddLabel(TableLayoutPanel panel, int row, string text)
    {
        panel.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        panel.Controls.Add(CreateFieldLabel(text), 0, row);
    }

    private static Label CreateFieldLabel(string text) => new()
    {
        Text = text,
        AutoSize = true,
        Anchor = AnchorStyles.Left,
        Padding = new Padding(0, 6, 16, 6)
    };

    private static void ConfigureNumeric(NumericUpDown numeric, int min, int max)
    {
        numeric.Minimum = min;
        numeric.Maximum = max;
        numeric.DecimalPlaces = 0;
    }

    private static decimal Clamp(int value, NumericUpDown numeric)
        => Math.Min(numeric.Maximum, Math.Max(numeric.Minimum, value));

    private sealed class SourceRowModel
    {
        public string SourceKey { get; set; } = string.Empty;
        public string DisplayName { get; set; } = string.Empty;
        public string Url { get; set; } = string.Empty;
        public IndicatorType IndicatorType { get; set; }
        public string ProviderKey { get; set; } = string.Empty;
        public bool Enabled { get; set; }
        public bool Required { get; set; }

        public static SourceRowModel From(SourceDefinitionSetting source) => new()
        {
            SourceKey = source.SourceKey,
            DisplayName = source.DisplayName,
            Url = source.Url,
            IndicatorType = source.IndicatorType,
            ProviderKey = source.ProviderKey,
            Enabled = source.Enabled,
            Required = source.Required
        };

        public SourceDefinitionSetting ToSetting() => new()
        {
            SourceKey = string.IsNullOrWhiteSpace(SourceKey) ? $"CUSTOM_{Guid.NewGuid():N}".ToUpperInvariant() : SourceKey.Trim(),
            DisplayName = DisplayName.Trim(),
            Url = Url.Trim(),
            IndicatorType = IndicatorType,
            ProviderKey = ProviderKey.Trim(),
            Enabled = Enabled,
            Required = Required
        };
    }
}
