using System.ComponentModel;
using YiHeLee.Application.Abstractions;
using YiHeLee.Domain;

namespace YiHeLee.App.Forms;

/// <summary>
/// 「歷史收盤價」查詢畫面：市場別／股票代碼或名稱／日期區間／交易日數篩選，分頁顯示官方收盤價與
/// 自算 MA5／MA20／MA60／MA120（資料不足時顯示文字，不得顯示0）。「立即回補」建立並執行歷史回補批次，
/// 下方即時顯示整體與工作明細進度；「重新整理」畫面（重新開啟本表單）仍可從資料庫回復目前進度。
/// </summary>
internal sealed class HistoricalPriceForm : Form
{
    private const int PageSize = 50;

    private readonly IMarketDataRepository _marketDataRepository;
    private readonly IStockHistoryImportService _importService;
    private readonly IStockPriceImportRepository _importRepository;
    private readonly ISettingsStore _settingsStore;
    private readonly IAppLogger _logger;
    private readonly ICrawlerRegistry _crawlerRegistry;
    private readonly IStockPriceValidationService _validationService;

    private readonly ComboBox _marketCombo = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 120 };
    private readonly TextBox _keywordText = new() { Width = 200 };
    private readonly CheckBox _useStartDate = new() { Text = "開始日期", AutoSize = true };
    private readonly DateTimePicker _startDate = new() { Format = DateTimePickerFormat.Short, Width = 120, Enabled = false };
    private readonly CheckBox _useEndDate = new() { Text = "結束日期", AutoSize = true };
    private readonly DateTimePicker _endDate = new() { Format = DateTimePickerFormat.Short, Width = 120, Enabled = false };
    private readonly ComboBox _tradingDaysPreset = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 100 };
    private readonly NumericUpDown _customTradingDays = new() { Minimum = 1, Maximum = 250, Value = 5, Width = 70, Enabled = false };
    private readonly Label _dataSourceLabel = new()
    {
        AutoSize = true,
        Text = "資料來源：官方 TWSE／TPEx（依市場別自動對應）｜同時勾選開始／結束日期時，「立即回補」改依此日期區間回補，不再使用「最近交易日數」"
    };

    private readonly Button _queryButton = new() { Text = "查詢", AutoSize = true };
    private readonly Button _clearButton = new() { Text = "清除", AutoSize = true };
    private readonly Button _importButton = new() { Text = "立即回補", AutoSize = true };
    private readonly Button _cancelButton = new() { Text = "取消抓取", AutoSize = true, Enabled = false };
    private readonly Button _reloadButton = new() { Text = "重新載入", AutoSize = true };
    private readonly Button _validateButton = new() { Text = "與鉅亨網交叉驗證", AutoSize = true };

    private readonly BindingList<HistoricalPriceRowViewModel> _rows = [];
    private readonly DataGridView _resultsGrid = new();
    private readonly Label _pageInfoLabel = new() { AutoSize = true, Text = "尚未查詢" };
    private readonly Button _prevPageButton = new() { Text = "◀ 上一頁", AutoSize = true };
    private readonly Button _nextPageButton = new() { Text = "下一頁 ▶", AutoSize = true };

    private readonly Label _jobSummaryLabel = new() { Dock = DockStyle.Top, Height = 46, AutoSize = false, TextAlign = ContentAlignment.MiddleLeft };
    private readonly BindingList<TaskProgressViewModel> _taskRows = [];
    private readonly DataGridView _taskGrid = new();
    private readonly System.Windows.Forms.Timer _progressTimer = new() { Interval = 1000 };

    private int _currentPage = 1;
    private int _totalCount;
    private long? _currentJobId;
    private CancellationTokenSource? _currentCts;
    private bool _isRefreshingProgress;

    public HistoricalPriceForm(
        IMarketDataRepository marketDataRepository,
        IStockHistoryImportService importService,
        IStockPriceImportRepository importRepository,
        ISettingsStore settingsStore,
        IAppLogger logger,
        ICrawlerRegistry crawlerRegistry,
        IStockPriceValidationService validationService)
    {
        _marketDataRepository = marketDataRepository;
        _importService = importService;
        _importRepository = importRepository;
        _settingsStore = settingsStore;
        _logger = logger;
        _crawlerRegistry = crawlerRegistry;
        _validationService = validationService;

        Text = "Yi He Lee－歷史收盤價";
        StartPosition = FormStartPosition.CenterScreen;
        // 與 MainForm 相同：尺寸以 96 DPI 為基準，高 DPI 螢幕下整體等比放大，避免破版。
        AutoScaleDimensions = new SizeF(96F, 96F);
        AutoScaleMode = AutoScaleMode.Dpi;
        MinimumSize = new Size(1080, 760);
        Size = new Size(1280, 860);
        Font = new Font("Microsoft JhengHei UI", 10F);
        Icon = SystemIcons.Application;

        // 依實際字型量測日期欄需要的寬度（最寬日期＋下拉鈕＋邊框），文字放大時日期才不會被截斷。
        var dateWidth = TextRenderer.MeasureText("2026/12/31", Font).Width
            + SystemInformation.VerticalScrollBarWidth + 16;
        _startDate.Width = dateWidth;
        _endDate.Width = dateWidth;

        var root = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 4 };
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 62));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 38));

        root.Controls.Add(BuildFilterPanel(), 0, 0);
        root.Controls.Add(BuildResultsPanel(), 0, 1);
        root.Controls.Add(BuildPaginationPanel(), 0, 2);
        root.Controls.Add(BuildProgressPanel(), 0, 3);
        Controls.Add(root);

        _marketCombo.Items.AddRange(["全部", "上市", "上櫃"]);
        _marketCombo.SelectedIndex = 0;

        _tradingDaysPreset.Items.AddRange(["5日", "10日", "20日", "60日", "120日", "自訂"]);
        _tradingDaysPreset.SelectedIndex = 0;
        _tradingDaysPreset.SelectedIndexChanged += (_, _) =>
        {
            _customTradingDays.Enabled = _tradingDaysPreset.SelectedItem?.ToString() == "自訂";
        };

        _useStartDate.CheckedChanged += (_, _) => _startDate.Enabled = _useStartDate.Checked;
        _useEndDate.CheckedChanged += (_, _) => _endDate.Enabled = _useEndDate.Checked;

        _queryButton.Click += async (_, _) => await RunQueryAsync(resetPage: true);
        _clearButton.Click += (_, _) => ClearFilters();
        _importButton.Click += async (_, _) => await StartImportAsync();
        _cancelButton.Click += async (_, _) => await CancelImportAsync();
        _reloadButton.Click += async (_, _) => await ReloadAsync();
        _validateButton.Click += async (_, _) => await RunCrossValidationAsync();
        _prevPageButton.Click += async (_, _) => { if (_currentPage > 1) { _currentPage--; await RunQueryAsync(resetPage: false); } };
        _nextPageButton.Click += async (_, _) => { if (_currentPage * PageSize < _totalCount) { _currentPage++; await RunQueryAsync(resetPage: false); } };

        _progressTimer.Tick += async (_, _) => await RefreshProgressAsync();

        Load += async (_, _) => await OnLoadAsync();
        FormClosed += (_, _) => _progressTimer.Stop();
    }

    private Control BuildFilterPanel()
    {
        var group = new GroupBox { Text = "查詢條件", Dock = DockStyle.Top, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, Padding = new Padding(12, 8, 12, 12) };
        var layout = new FlowLayoutPanel { Dock = DockStyle.Top, FlowDirection = FlowDirection.TopDown, AutoSize = true, WrapContents = false };

        var filterRow = new FlowLayoutPanel { AutoSize = true, WrapContents = true, Margin = new Padding(0, 4, 0, 8) };

        void AddLabeled(string text, Control control)
        {
            filterRow.Controls.Add(new Label { Text = text, AutoSize = true, Anchor = AnchorStyles.Left, Margin = new Padding(0, 8, 4, 0) });
            control.Margin = new Padding(0, 3, 16, 0);
            filterRow.Controls.Add(control);
        }

        AddLabeled("市場別", _marketCombo);
        AddLabeled("股票代碼或名稱", _keywordText);
        _useStartDate.Margin = new Padding(0, 6, 4, 0);
        _startDate.Margin = new Padding(0, 3, 16, 0);
        _useEndDate.Margin = new Padding(0, 6, 4, 0);
        _endDate.Margin = new Padding(0, 3, 16, 0);
        filterRow.Controls.Add(_useStartDate);
        filterRow.Controls.Add(_startDate);
        filterRow.Controls.Add(_useEndDate);
        filterRow.Controls.Add(_endDate);
        AddLabeled("最近交易日數", _tradingDaysPreset);
        _customTradingDays.Margin = new Padding(0, 3, 0, 0);
        filterRow.Controls.Add(_customTradingDays);

        var buttonRow = new FlowLayoutPanel { AutoSize = true, Margin = new Padding(0) };
        foreach (var button in new[] { _queryButton, _clearButton, _importButton, _cancelButton, _reloadButton, _validateButton })
        {
            button.AutoSize = true;
            button.MinimumSize = new Size(100, 34);
            button.Padding = new Padding(12, 0, 12, 0);
            button.TextAlign = ContentAlignment.MiddleCenter;
            button.Margin = new Padding(0, 0, 10, 0);
            buttonRow.Controls.Add(button);
        }

        _dataSourceLabel.Margin = new Padding(0, 10, 0, 0);
        _dataSourceLabel.ForeColor = SystemColors.GrayText;

        layout.Controls.Add(filterRow);
        layout.Controls.Add(buttonRow);
        layout.Controls.Add(_dataSourceLabel);
        group.Controls.Add(layout);

        return group;
    }

    private Control BuildResultsPanel()
    {
        _resultsGrid.Dock = DockStyle.Fill;
        _resultsGrid.AutoGenerateColumns = false;
        _resultsGrid.ReadOnly = true;
        _resultsGrid.AllowUserToAddRows = false;
        _resultsGrid.AllowUserToDeleteRows = false;
        _resultsGrid.RowHeadersVisible = false;
        _resultsGrid.SelectionMode = DataGridViewSelectionMode.FullRowSelect;
        _resultsGrid.DataSource = _rows;

        AddColumn(_resultsGrid, nameof(HistoricalPriceRowViewModel.TradeDate), "交易日期", 90);
        AddColumn(_resultsGrid, nameof(HistoricalPriceRowViewModel.MarketType), "市場別", 60);
        AddColumn(_resultsGrid, nameof(HistoricalPriceRowViewModel.StockCode), "股票代碼", 70);
        AddColumn(_resultsGrid, nameof(HistoricalPriceRowViewModel.StockName), "股票名稱", 110);
        AddColumn(_resultsGrid, nameof(HistoricalPriceRowViewModel.OpenPrice), "開盤價", 70);
        AddColumn(_resultsGrid, nameof(HistoricalPriceRowViewModel.HighPrice), "最高價", 70);
        AddColumn(_resultsGrid, nameof(HistoricalPriceRowViewModel.LowPrice), "最低價", 70);
        AddColumn(_resultsGrid, nameof(HistoricalPriceRowViewModel.ClosePrice), "收盤價", 70);
        AddColumn(_resultsGrid, nameof(HistoricalPriceRowViewModel.TradeVolume), "成交量", 90);
        AddColumn(_resultsGrid, nameof(HistoricalPriceRowViewModel.PriceChange), "漲跌價差", 80);
        AddColumn(_resultsGrid, nameof(HistoricalPriceRowViewModel.Ma5), "MA5", 70);
        AddColumn(_resultsGrid, nameof(HistoricalPriceRowViewModel.Ma20), "MA20", 70);
        AddColumn(_resultsGrid, nameof(HistoricalPriceRowViewModel.Ma60), "MA60", 70);
        AddColumn(_resultsGrid, nameof(HistoricalPriceRowViewModel.Ma120), "MA120", 70);
        AddColumn(_resultsGrid, nameof(HistoricalPriceRowViewModel.SourceProvider), "資料來源", 80);
        AddColumn(_resultsGrid, nameof(HistoricalPriceRowViewModel.IsOfficial), "官方資料", 70);
        AddColumn(_resultsGrid, nameof(HistoricalPriceRowViewModel.FetchedAt), "擷取時間", 130);

        var panel = new Panel { Dock = DockStyle.Fill, Padding = new Padding(8, 0, 8, 0) };
        panel.Controls.Add(_resultsGrid);
        return panel;
    }

    private Control BuildPaginationPanel()
    {
        var panel = new FlowLayoutPanel { Dock = DockStyle.Top, AutoSize = true, Padding = new Padding(8, 6, 8, 6) };
        _prevPageButton.AutoSize = true;
        _prevPageButton.MinimumSize = new Size(90, 30);
        _prevPageButton.Margin = new Padding(0, 0, 6, 0);
        _nextPageButton.AutoSize = true;
        _nextPageButton.MinimumSize = new Size(90, 30);
        _nextPageButton.Margin = new Padding(0, 0, 12, 0);
        panel.Controls.Add(_prevPageButton);
        panel.Controls.Add(_nextPageButton);
        _pageInfoLabel.Padding = new Padding(0, 8, 0, 0);
        panel.Controls.Add(_pageInfoLabel);
        return panel;
    }

    private Control BuildProgressPanel()
    {
        var group = new GroupBox { Dock = DockStyle.Fill, Text = "抓取進度", Padding = new Padding(8) };
        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, RowCount = 2, ColumnCount = 1 };
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        layout.Controls.Add(_jobSummaryLabel, 0, 0);

        _taskGrid.Dock = DockStyle.Fill;
        _taskGrid.AutoGenerateColumns = false;
        _taskGrid.ReadOnly = true;
        _taskGrid.AllowUserToAddRows = false;
        _taskGrid.AllowUserToDeleteRows = false;
        _taskGrid.RowHeadersVisible = false;
        _taskGrid.DataSource = _taskRows;

        AddColumn(_taskGrid, nameof(TaskProgressViewModel.MarketType), "市場別", 60);
        AddColumn(_taskGrid, nameof(TaskProgressViewModel.RequestedDate), "要求日期", 90);
        AddColumn(_taskGrid, nameof(TaskProgressViewModel.ActualTradeDate), "實際交易日期", 100);
        AddColumn(_taskGrid, nameof(TaskProgressViewModel.Status), "狀態", 90);
        AddColumn(_taskGrid, nameof(TaskProgressViewModel.RetryCount), "重試次數", 70);
        AddColumn(_taskGrid, nameof(TaskProgressViewModel.TotalRows), "總筆數", 60);
        AddColumn(_taskGrid, nameof(TaskProgressViewModel.InsertedRows), "新增筆數", 70);
        AddColumn(_taskGrid, nameof(TaskProgressViewModel.UpdatedRows), "更新筆數", 70);
        AddColumn(_taskGrid, nameof(TaskProgressViewModel.SkippedRows), "略過筆數", 70);
        AddColumn(_taskGrid, nameof(TaskProgressViewModel.FailedRows), "失敗筆數", 70);
        AddColumn(_taskGrid, nameof(TaskProgressViewModel.ProgressPercent), "進度%", 60);
        AddColumn(_taskGrid, nameof(TaskProgressViewModel.StartedAt), "開始時間", 110);
        AddColumn(_taskGrid, nameof(TaskProgressViewModel.CompletedAt), "完成時間", 110);
        AddColumn(_taskGrid, nameof(TaskProgressViewModel.ErrorMessage), "錯誤訊息", 220);

        layout.Controls.Add(_taskGrid, 0, 1);
        group.Controls.Add(layout);
        return group;
    }

    private static void AddColumn(DataGridView grid, string propertyName, string header, int width)
    {
        grid.Columns.Add(new DataGridViewTextBoxColumn
        {
            DataPropertyName = propertyName,
            HeaderText = header,
            Width = width,
            SortMode = DataGridViewColumnSortMode.NotSortable
        });
    }

    private async Task OnLoadAsync()
    {
        try
        {
            var latestJob = await _importRepository.GetLatestJobProgressAsync(CancellationToken.None).ConfigureAwait(true);
            if (latestJob is not null)
            {
                _currentJobId = latestJob.JobId;
                await RefreshProgressAsync().ConfigureAwait(true);
                if (IsJobActive(latestJob.Status))
                {
                    _progressTimer.Start();
                    _cancelButton.Enabled = true;
                }
            }

            await RunQueryAsync(resetPage: true).ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            _logger.Error("開啟歷史收盤價畫面時載入初始資料失敗。", ex);
        }
    }

    private void ClearFilters()
    {
        _marketCombo.SelectedIndex = 0;
        _keywordText.Text = string.Empty;
        _useStartDate.Checked = false;
        _useEndDate.Checked = false;
        _tradingDaysPreset.SelectedIndex = 0;
        _customTradingDays.Value = 5;
    }

    private MarketScope SelectedScope => _marketCombo.SelectedIndex switch
    {
        1 => MarketScope.Listed,
        2 => MarketScope.Otc,
        _ => MarketScope.All
    };

    private int SelectedTradingDays => _tradingDaysPreset.SelectedItem?.ToString() switch
    {
        "5日" => 5,
        "10日" => 10,
        "20日" => 20,
        "60日" => 60,
        "120日" => 120,
        _ => Decimal.ToInt32(_customTradingDays.Value)
    };

    private async Task RunQueryAsync(bool resetPage)
    {
        if (resetPage)
        {
            _currentPage = 1;
        }

        try
        {
            DateOnly? startDate = null;
            DateOnly? endDate = null;
            if (_useStartDate.Checked)
            {
                startDate = DateOnly.FromDateTime(_startDate.Value.Date);
            }

            if (_useEndDate.Checked)
            {
                endDate = DateOnly.FromDateTime(_endDate.Value.Date);
            }

            var filter = new StockDailyPriceQueryFilter(SelectedScope, _keywordText.Text, startDate, endDate, _currentPage, PageSize);
            var result = await _marketDataRepository.QueryDailyPricesAsync(filter, CancellationToken.None).ConfigureAwait(true);

            _rows.Clear();
            foreach (var row in result.Rows)
            {
                _rows.Add(HistoricalPriceRowViewModel.From(row));
            }

            _totalCount = result.TotalCount;
            var totalPages = _totalCount == 0 ? 1 : (int)Math.Ceiling(_totalCount / (double)PageSize);
            _pageInfoLabel.Text = $"第 {_currentPage} / {totalPages} 頁，共 {_totalCount} 筆";
            _prevPageButton.Enabled = _currentPage > 1;
            _nextPageButton.Enabled = _currentPage * PageSize < _totalCount;
        }
        catch (Exception ex)
        {
            _logger.Error("查詢歷史收盤價失敗。", ex);
            MessageBox.Show(this, $"查詢失敗：{ex.Message}", "Yi He Lee", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    private async Task ReloadAsync()
    {
        await RefreshProgressAsync().ConfigureAwait(true);
        await RunQueryAsync(resetPage: false).ConfigureAwait(true);
    }

    private async Task StartImportAsync()
    {
        if (_currentJobId is not null && _currentCts is not null && !_currentCts.IsCancellationRequested)
        {
            MessageBox.Show(this, "目前已有回補批次正在執行，請先等待完成或取消。", "Yi He Lee", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        if (_useStartDate.Checked != _useEndDate.Checked)
        {
            MessageBox.Show(this, "指定日期區間回補時，開始日期與結束日期必須同時勾選。", "Yi He Lee", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        _importButton.Enabled = false;
        try
        {
            var settings = await _settingsStore.LoadAsync(CancellationToken.None).ConfigureAwait(true);
            var importOptions = settings.StockHistoryImport;
            StockHistoryImportRequest request;
            if (_useStartDate.Checked && _useEndDate.Checked)
            {
                var startDate = DateOnly.FromDateTime(_startDate.Value.Date);
                var endDate = DateOnly.FromDateTime(_endDate.Value.Date);
                request = new StockHistoryImportRequest(SelectedScope, importOptions.DefaultTradingDays, startDate, endDate);
            }
            else
            {
                var tradingDays = importOptions.ClampTradingDays(SelectedTradingDays);
                request = new StockHistoryImportRequest(SelectedScope, tradingDays);
            }

            var jobId = await _importService.CreateJobAsync(request, importOptions, CancellationToken.None).ConfigureAwait(true);
            _currentJobId = jobId;
            _currentCts = new CancellationTokenSource();
            _cancelButton.Enabled = true;
            _progressTimer.Start();

            var cts = _currentCts;
            _ = Task.Run(async () =>
            {
                try
                {
                    await _importService.RunJobAsync(jobId, settings.OfficialMarketData, importOptions, cts.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    // 取消為正常操作路徑，批次狀態已由 Service 內部標記為 Cancelled。
                }
                catch (Exception ex)
                {
                    _logger.Error($"歷史收盤價回補批次 {jobId} 執行時發生未預期錯誤。", ex);
                }
            });

            await RefreshProgressAsync().ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            _logger.Error("建立歷史收盤價回補批次失敗。", ex);
            MessageBox.Show(this, $"建立回補批次失敗：{ex.Message}", "Yi He Lee", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
        finally
        {
            _importButton.Enabled = true;
        }
    }

    private async Task CancelImportAsync()
    {
        if (_currentCts is null)
        {
            return;
        }

        _cancelButton.Enabled = false;
        try
        {
            await _currentCts.CancelAsync().ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            _logger.Error("取消歷史收盤價回補批次失敗。", ex);
        }
    }

    private async Task RefreshProgressAsync()
    {
        if (_currentJobId is not { } jobId || _isRefreshingProgress)
        {
            // 上一次輪詢尚未完成時略過本次 Tick，避免計時器在查詢耗時較長時重疊觸發。
            return;
        }

        _isRefreshingProgress = true;
        try
        {
            var job = await _importRepository.GetJobProgressAsync(jobId, CancellationToken.None).ConfigureAwait(true);
            if (job is null)
            {
                return;
            }

            _jobSummaryLabel.Text =
                $"批次 #{job.JobId}｜{DescribeJobType(job.JobType)}｜要求交易日數 {job.RequestedTradingDays}｜" +
                $"工作 {job.CompletedTasks}/{job.TotalTasks}（成功 {job.SuccessTasks}／略過 {job.SkippedTasks}／失敗 {job.FailedTasks}）｜" +
                $"資料 已處理 {job.ProcessedRows}（新增 {job.InsertedRows}／更新 {job.UpdatedRows}／略過 {job.SkippedRows}／失敗 {job.FailedRows}）｜" +
                $"進度 {job.ProgressPercent:0.0}%｜狀態：{DescribeJobStatus(job.Status)}" +
                (job.ErrorMessage is null ? string.Empty : $"｜{job.ErrorMessage}");

            var taskList = await _importRepository.GetTaskProgressAsync(jobId, CancellationToken.None).ConfigureAwait(true);
            _taskRows.Clear();
            foreach (var task in taskList)
            {
                _taskRows.Add(TaskProgressViewModel.From(task));
            }

            if (!IsJobActive(job.Status))
            {
                _progressTimer.Stop();
                _cancelButton.Enabled = false;
                if (_currentPage >= 1)
                {
                    await RunQueryAsync(resetPage: false).ConfigureAwait(true);
                }
            }
        }
        catch (Exception ex)
        {
            _logger.Error("更新歷史收盤價回補進度失敗。", ex);
        }
        finally
        {
            _isRefreshingProgress = false;
        }
    }

    /// <summary>
    /// 以目前資料庫最新交易日期，取得鉅亨網多頭／空頭排列（集中＋店頭）清單，
    /// 與本系統依官方收盤價自算的均線交叉驗證。鉅亨網資料僅供比對，不覆蓋官方資料；
    /// 只比對清單內出現的股票，其餘股票不受影響（不代表計算錯誤）。
    /// </summary>
    private async Task RunCrossValidationAsync()
    {
        _validateButton.Enabled = false;
        try
        {
            var latestTradeDate = await _marketDataRepository.GetLatestTradeDateAsync(CancellationToken.None).ConfigureAwait(true);
            if (latestTradeDate is null)
            {
                MessageBox.Show(this, "資料庫尚無官方歷史收盤價，請先執行「立即回補」。", "Yi He Lee", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            var settings = await _settingsStore.LoadAsync(CancellationToken.None).ConfigureAwait(true);
            var cnyesSources = settings.Sources
                .Where(x => x.Enabled && x.ProviderKey == "CnyesTechnicalAlignment")
                .Select(x => x.ToDomain())
                .ToArray();

            var batches = new List<CrawlBatch>();
            foreach (var source in cnyesSources)
            {
                foreach (var market in new[] { MarketType.Listed, MarketType.Otc })
                {
                    try
                    {
                        var crawler = _crawlerRegistry.Resolve(source.ProviderKey);
                        var batch = await crawler.CrawlAsync(source, market, latestTradeDate.Value, settings, CancellationToken.None).ConfigureAwait(true);
                        batches.Add(batch);
                    }
                    catch (Exception ex)
                    {
                        _logger.Warning($"交叉驗證擷取鉅亨網 {source.DisplayName}／{(market == MarketType.Listed ? "集中市場" : "店頭市場")} 失敗，本次不影響官方資料：{ex.Message}");
                    }
                }
            }

            if (batches.Count == 0)
            {
                MessageBox.Show(this, "本次未能取得鉅亨網多頭／空頭排列清單，無法交叉驗證（不影響官方資料）。", "Yi He Lee", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            var codes = batches.SelectMany(b => b.Items.Select(i => i.StockCode)).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
            var stockMarketTypes = await _marketDataRepository.GetStockMarketTypesAsync(codes, CancellationToken.None).ConfigureAwait(true);

            var records = await _validationService.ValidateAsync(latestTradeDate.Value, stockMarketTypes, batches, CancellationToken.None).ConfigureAwait(true);

            var matched = records.Count(x => x.Outcome == CnyesValidationOutcome.Matched);
            var mismatched = records.Count(x => x.Outcome == CnyesValidationOutcome.Mismatched);
            var insufficient = records.Count(x => x.Outcome == CnyesValidationOutcome.InsufficientHistory);
            var notApplicable = records.Count(x => x.Outcome == CnyesValidationOutcome.NotApplicable);
            var dateMismatch = records.Count(x => x.Outcome == CnyesValidationOutcome.SourceDateMismatch);

            MessageBox.Show(
                this,
                $"交易日期：{latestTradeDate:yyyy-MM-dd}\r\n相符：{matched}\r\n差異：{mismatched}\r\n資料不足：{insufficient}\r\n不適用（未在清單中）：{notApplicable}\r\n鉅亨頁面日期不符：{dateMismatch}",
                "Yi He Lee－鉅亨網交叉驗證結果",
                MessageBoxButtons.OK,
                mismatched > 0 ? MessageBoxIcon.Warning : MessageBoxIcon.Information);
        }
        catch (Exception ex)
        {
            _logger.Error("鉅亨網交叉驗證失敗。", ex);
            MessageBox.Show(this, $"交叉驗證失敗：{ex.Message}", "Yi He Lee", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
        finally
        {
            _validateButton.Enabled = true;
        }
    }

    private static bool IsJobActive(StockPriceImportJobStatus status)
        => status is StockPriceImportJobStatus.Queued or StockPriceImportJobStatus.Running or StockPriceImportJobStatus.WaitingForSource;

    private static string DescribeJobType(OfficialPriceJobType jobType)
        => jobType == OfficialPriceJobType.HistoricalBackfill ? "歷史回補" : "每日排程";

    private static string DescribeJobStatus(StockPriceImportJobStatus status) => status switch
    {
        StockPriceImportJobStatus.Queued => "排隊中",
        StockPriceImportJobStatus.Running => "執行中",
        StockPriceImportJobStatus.WaitingForSource => "等待來源更新",
        StockPriceImportJobStatus.Completed => "完成",
        StockPriceImportJobStatus.CompletedWithErrors => "部分失敗",
        StockPriceImportJobStatus.Failed => "失敗",
        StockPriceImportJobStatus.Cancelled => "已取消",
        _ => status.ToString()
    };

    private static string DescribeTaskStatus(StockPriceImportTaskStatus status) => status switch
    {
        StockPriceImportTaskStatus.Queued => "排隊中",
        StockPriceImportTaskStatus.Running => "執行中",
        StockPriceImportTaskStatus.WaitingForSource => "等待來源更新",
        StockPriceImportTaskStatus.Succeeded => "成功",
        StockPriceImportTaskStatus.Holiday => "休市／無資料",
        StockPriceImportTaskStatus.Failed => "失敗",
        StockPriceImportTaskStatus.Cancelled => "已取消",
        _ => status.ToString()
    };

    private sealed class HistoricalPriceRowViewModel
    {
        public string TradeDate { get; init; } = string.Empty;
        public string MarketType { get; init; } = string.Empty;
        public string StockCode { get; init; } = string.Empty;
        public string StockName { get; init; } = string.Empty;
        public string OpenPrice { get; init; } = string.Empty;
        public string HighPrice { get; init; } = string.Empty;
        public string LowPrice { get; init; } = string.Empty;
        public string ClosePrice { get; init; } = string.Empty;
        public string TradeVolume { get; init; } = string.Empty;
        public string PriceChange { get; init; } = string.Empty;
        public string SourceProvider { get; init; } = string.Empty;
        public string IsOfficial { get; init; } = string.Empty;
        public string FetchedAt { get; init; } = string.Empty;
        public string Ma5 { get; init; } = string.Empty;
        public string Ma20 { get; init; } = string.Empty;
        public string Ma60 { get; init; } = string.Empty;
        public string Ma120 { get; init; } = string.Empty;

        public static HistoricalPriceRowViewModel From(StockDailyPriceQueryRow row) => new()
        {
            TradeDate = row.TradeDate.ToString("yyyy-MM-dd"),
            MarketType = row.MarketType switch
            {
                YiHeLee.Domain.MarketType.Listed => "上市",
                YiHeLee.Domain.MarketType.Otc => "上櫃",
                YiHeLee.Domain.MarketType.Emerging => "興櫃",
                _ => row.MarketType.ToString()
            },
            StockCode = row.StockCode,
            StockName = row.StockName,
            OpenPrice = FormatPrice(row.OpenPrice),
            HighPrice = FormatPrice(row.HighPrice),
            LowPrice = FormatPrice(row.LowPrice),
            ClosePrice = row.ClosePrice.ToString("0.00"),
            TradeVolume = row.TradeVolume?.ToString("#,0") ?? string.Empty,
            PriceChange = FormatPrice(row.PriceChange),
            SourceProvider = row.SourceProvider,
            IsOfficial = row.IsOfficial ? "是" : "否",
            FetchedAt = row.FetchedAt.ToString("yyyy-MM-dd HH:mm"),
            Ma5 = FormatMa(row.MovingAverage5),
            Ma20 = FormatMa(row.MovingAverage20),
            Ma60 = FormatMa(row.MovingAverage60),
            Ma120 = FormatMa(row.MovingAverage120)
        };

        private static string FormatPrice(decimal? value) => value?.ToString("0.00") ?? string.Empty;

        // 資料不足時必須顯示文字，不得顯示0，避免使用者誤判為實際均價為零。
        private static string FormatMa(decimal? value) => value?.ToString("0.00") ?? "資料不足";
    }

    private sealed class TaskProgressViewModel
    {
        public string MarketType { get; init; } = string.Empty;
        public string RequestedDate { get; init; } = string.Empty;
        public string ActualTradeDate { get; init; } = string.Empty;
        public string Status { get; init; } = string.Empty;
        public int RetryCount { get; init; }
        public int TotalRows { get; init; }
        public int InsertedRows { get; init; }
        public int UpdatedRows { get; init; }
        public int SkippedRows { get; init; }
        public int FailedRows { get; init; }
        public string ProgressPercent { get; init; } = string.Empty;
        public string StartedAt { get; init; } = string.Empty;
        public string CompletedAt { get; init; } = string.Empty;
        public string ErrorMessage { get; init; } = string.Empty;

        public static TaskProgressViewModel From(StockPriceImportTaskProgress task) => new()
        {
            MarketType = task.MarketType == YiHeLee.Domain.MarketType.Listed ? "上市" : "上櫃",
            RequestedDate = task.RequestedDate.ToString("yyyy-MM-dd"),
            ActualTradeDate = task.ActualTradeDate?.ToString("yyyy-MM-dd") ?? string.Empty,
            Status = DescribeTaskStatus(task.Status),
            RetryCount = task.RetryCount,
            TotalRows = task.TotalRows,
            InsertedRows = task.InsertedRows,
            UpdatedRows = task.UpdatedRows,
            SkippedRows = task.SkippedRows,
            FailedRows = task.FailedRows,
            ProgressPercent = task.ProgressPercent.ToString("0"),
            StartedAt = task.StartedAt?.ToString("HH:mm:ss") ?? string.Empty,
            CompletedAt = task.CompletedAt?.ToString("HH:mm:ss") ?? string.Empty,
            ErrorMessage = task.ErrorMessage ?? string.Empty
        };
    }
}
