using System.ComponentModel;
using YiHeLee.Application.Services;
using YiHeLee.Domain;

namespace YiHeLee.App.Forms;

internal sealed class SettingsForm : Form
{
    private readonly SettingsValidationService _validationService;
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
    private readonly TextBox _excludedColors = new();
    private readonly TextBox _excludedMarkers = new();
    private readonly BindingList<SourceRowModel> _sourceRows = [];
    private readonly DataGridView _sourcesGrid = new();

    public SettingsForm(AppSettings settings, SettingsValidationService validationService, bool isAdministrator)
    {
        _validationService = validationService;
        Text = "Yi He Lee－設定";
        StartPosition = FormStartPosition.CenterScreen;
        MinimumSize = new Size(900, 650);
        Size = new Size(1050, 760);
        Font = new Font("Microsoft JhengHei UI", 10F);
        Icon = isAdministrator ? SystemIcons.Shield : SystemIcons.Application;

        var tabs = new TabControl { Dock = DockStyle.Fill };
        tabs.TabPages.Add(BuildGeneralTab(isAdministrator));
        tabs.TabPages.Add(BuildSourcesTab());

        var saveButton = new Button { Text = "儲存", AutoSize = true, Padding = new Padding(16, 5, 16, 5) };
        saveButton.Click += (_, _) => SaveAndClose();
        var cancelButton = new Button { Text = "取消", AutoSize = true, Padding = new Padding(16, 5, 16, 5), DialogResult = DialogResult.Cancel };

        var buttons = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.RightToLeft,
            AutoSize = true,
            Padding = new Padding(8)
        };
        buttons.Controls.Add(saveButton);
        buttons.Controls.Add(cancelButton);

        var root = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 2
        };
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.Controls.Add(tabs, 0, 0);
        root.Controls.Add(buttons, 0, 1);
        Controls.Add(root);

        AcceptButton = saveButton;
        CancelButton = cancelButton;
        LoadSettings(settings);
    }

    public AppSettings? ResultSettings { get; private set; }

    private TabPage BuildGeneralTab(bool isAdministrator)
    {
        var tab = new TabPage("一般設定");
        var panel = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            AutoScroll = true,
            ColumnCount = 3,
            Padding = new Padding(18)
        };
        panel.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 230));
        panel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        panel.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));

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

        ConfigureNumeric(_retryMinutes, 1, 240);
        AddNumericRow(panel, ref row, "網站／Excel 長時間重試間隔（分鐘）", _retryMinutes);
        ConfigureNumeric(_maximumAttempts, 1, 100);
        AddNumericRow(panel, ref row, "每日最大執行次數", _maximumAttempts);
        ConfigureNumeric(_crawlerRetryCount, 1, 20);
        AddNumericRow(panel, ref row, "每次爬蟲短暫重試次數", _crawlerRetryCount);
        ConfigureNumeric(_crawlerRetryDelay, 1, 120);
        AddNumericRow(panel, ref row, "爬蟲短暫重試等待（秒）", _crawlerRetryDelay);
        ConfigureNumeric(_excelRetryCount, 1, 20);
        AddNumericRow(panel, ref row, "Excel 忙碌短暫重試次數", _excelRetryCount);
        ConfigureNumeric(_excelRetryDelay, 1, 120);
        AddNumericRow(panel, ref row, "Excel 忙碌重試等待（秒）", _excelRetryDelay);

        AddCheckBoxRow(panel, ref row, _startWithWindows, "登入 Windows 後自動啟動");
        AddCheckBoxRow(panel, ref row, _startMinimized, "啟動後只顯示在右下角系統匣");
        AddCheckBoxRow(panel, ref row, _backupBeforeWrite, "寫入 Excel 前建立備份");
        AddCheckBoxRow(panel, ref row, _createOutputSheet, "找不到輸出頁籤時自動建立");
        AddCheckBoxRow(panel, ref row, _showSafetyPrompt, "操作 Excel 前顯示防呆確認");

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

        var privilegeText = isAdministrator
            ? "目前：系統管理員模式。Excel 也必須用相同權限開啟，程式才找得到活頁簿。"
            : "目前：一般使用者模式（建議，與一般方式開啟的 Excel 權限相同）。右下角選單可選擇提升權限。";
        panel.Controls.Add(new Label
        {
            Text = privilegeText,
            AutoSize = true,
            MaximumSize = new Size(760, 0),
            ForeColor = isAdministrator ? Color.DarkRed : Color.DarkGreen,
            Padding = new Padding(0, 14, 0, 0)
        }, 0, row);
        panel.SetColumnSpan(panel.GetControlFromPosition(0, row), 3);

        tab.Controls.Add(panel);
        return tab;
    }

    private TabPage BuildSourcesTab()
    {
        var tab = new TabPage("資料來源網址");
        var note = new Label
        {
            Dock = DockStyle.Top,
            Height = 62,
            Padding = new Padding(12),
            Text = "固定兩個鉅亨網來源不可停用或刪除。可新增 N 個網址；不同網站若 HTML 結構不同，必須另外開發對應的 ProviderKey 爬蟲。",
            ForeColor = Color.DarkBlue
        };

        _sourcesGrid.Dock = DockStyle.Fill;
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

        var addButton = new Button { Text = "新增網址", AutoSize = true };
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
        var removeButton = new Button { Text = "刪除選取網址", AutoSize = true };
        removeButton.Click += (_, _) => RemoveSelectedSource();

        var buttons = new FlowLayoutPanel
        {
            Dock = DockStyle.Bottom,
            Height = 48,
            Padding = new Padding(8)
        };
        buttons.Controls.Add(addButton);
        buttons.Controls.Add(removeButton);

        tab.Controls.Add(_sourcesGrid);
        tab.Controls.Add(buttons);
        tab.Controls.Add(note);
        return tab;
    }

    private void LoadSettings(AppSettings settings)
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
        _excludedColors.Text = string.Join(", ", settings.ExcludedHoldingFillColors);
        _excludedMarkers.Text = string.Join(Environment.NewLine, settings.ExcludedHoldingTextMarkers);

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

    private void SaveAndClose()
    {
        _sourcesGrid.EndEdit();
        var settings = new AppSettings
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
            ExcludedHoldingFillColors = SplitValues(_excludedColors.Text),
            ExcludedHoldingTextMarkers = SplitValues(_excludedMarkers.Text),
            Sources = _sourceRows.Select(x => x.ToSetting()).ToList()
        };

        _validationService.EnsureFixedSources(settings);
        var errors = _validationService.Validate(settings);
        if (errors.Count > 0)
        {
            MessageBox.Show(this, string.Join(Environment.NewLine, errors), "設定有誤",
                MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        ResultSettings = settings;
        DialogResult = DialogResult.OK;
        Close();
    }

    private static List<string> SplitValues(string value) => value
        .Split([',', ';', '，', '；', '\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
        .Distinct(StringComparer.OrdinalIgnoreCase)
        .ToList();

    private static void AddLabel(TableLayoutPanel panel, int row, string text)
    {
        panel.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        panel.Controls.Add(new Label
        {
            Text = text,
            AutoSize = true,
            Anchor = AnchorStyles.Left,
            Padding = new Padding(0, 6, 0, 6)
        }, 0, row);
    }

    private static void AddNumericRow(TableLayoutPanel panel, ref int row, string label, NumericUpDown numeric)
    {
        AddLabel(panel, row, label);
        numeric.Width = 120;
        numeric.Anchor = AnchorStyles.Left;
        panel.Controls.Add(numeric, 1, row);
        row++;
    }

    private static void AddCheckBoxRow(TableLayoutPanel panel, ref int row, CheckBox checkBox, string text)
    {
        AddLabel(panel, row, string.Empty);
        checkBox.Text = text;
        checkBox.AutoSize = true;
        checkBox.Anchor = AnchorStyles.Left;
        panel.Controls.Add(checkBox, 1, row);
        panel.SetColumnSpan(checkBox, 2);
        row++;
    }

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
