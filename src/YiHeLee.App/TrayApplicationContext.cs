using System.Diagnostics;
using YiHeLee.App.Forms;
using YiHeLee.App.Infrastructure;
using YiHeLee.Application.Abstractions;
using YiHeLee.Application.Services;
using YiHeLee.Domain;

namespace YiHeLee.App;

/// <summary>常駐 Windows 右下角系統匣，負責 UI 互動；商業流程仍由 Application Service 執行。
/// 2026-07-13 盤中／收盤流程拆分：手動操作分為「立即執行盤中判斷」與「立即執行收盤更新」兩個明確項目；
/// 系統匣文字依時段顯示盤中監控／等待收盤／已完成／基準未就緒，不再只顯示「等待每日 13:35」。</summary>
internal sealed class TrayApplicationContext : ApplicationContext
{
    private readonly AppPaths _paths;
    private readonly IntradayMonitoringService _intradayMonitoringService;
    private readonly ClosePrecomputeJobService _closePrecomputeJobService;
    private readonly MarketWorkflowScheduleCoordinator _scheduleCoordinator;
    private readonly IClock _clock;
    private readonly ISettingsStore _settingsStore;
    private readonly SettingsValidationService _validationService;
    private readonly IYiHeLeeRepository _repository;
    private readonly WinFormsUserInteraction _userInteraction;
    private readonly WindowsStartupManager _startupManager;
    private readonly IAppLogger _logger;
    private readonly IMarketDataRepository _marketDataRepository;
    private readonly IStockHistoryImportService _stockHistoryImportService;
    private readonly IStockPriceImportRepository _stockPriceImportRepository;
    private readonly ICrawlerRegistry _crawlerRegistry;
    private readonly IStockPriceValidationService _stockPriceValidationService;
    private readonly Form _dispatcherForm;
    private readonly NotifyIcon _notifyIcon;
    private readonly ToolStripMenuItem _statusItem;
    private readonly ToolStripMenuItem _runIntradayItem;
    private readonly ToolStripMenuItem _runCloseItem;
    private readonly ToolStripMenuItem _settingsItem;
    private readonly ToolStripMenuItem _openExcelItem;
    private readonly ToolStripMenuItem _historicalPriceItem;
    private readonly ToolStripMenuItem _administratorItem;
    private readonly CancellationTokenSource _lifetimeCts = new();
    private Task? _manualRunTask;
    private MainForm? _mainForm;
    private HistoricalPriceForm? _historicalPriceForm;
    private bool _isExiting;

    // 最後一次狀態，供主視窗建立（或重建）時補上，避免顯示過時的「初始化中」。
    private string _lastStatusMessage = "初始化中";
    private int _lastStatusPercent;
    private MarketWorkflowStatusSnapshot? _lastWorkflowStatus;

    public TrayApplicationContext(
        string[] args,
        AppPaths paths,
        IntradayMonitoringService intradayMonitoringService,
        ClosePrecomputeJobService closePrecomputeJobService,
        MarketWorkflowScheduleCoordinator scheduleCoordinator,
        IClock clock,
        ISettingsStore settingsStore,
        SettingsValidationService validationService,
        IYiHeLeeRepository repository,
        WinFormsUserInteraction userInteraction,
        WindowsStartupManager startupManager,
        IAppLogger logger,
        IMarketDataRepository marketDataRepository,
        IStockHistoryImportService stockHistoryImportService,
        IStockPriceImportRepository stockPriceImportRepository,
        ICrawlerRegistry crawlerRegistry,
        IStockPriceValidationService stockPriceValidationService)
    {
        _paths = paths;
        _intradayMonitoringService = intradayMonitoringService;
        _closePrecomputeJobService = closePrecomputeJobService;
        _scheduleCoordinator = scheduleCoordinator;
        _clock = clock;
        _settingsStore = settingsStore;
        _validationService = validationService;
        _repository = repository;
        _userInteraction = userInteraction;
        _startupManager = startupManager;
        _logger = logger;
        _marketDataRepository = marketDataRepository;
        _stockHistoryImportService = stockHistoryImportService;
        _stockPriceImportRepository = stockPriceImportRepository;
        _crawlerRegistry = crawlerRegistry;
        _stockPriceValidationService = stockPriceValidationService;

        _dispatcherForm = new Form
        {
            ShowInTaskbar = false,
            FormBorderStyle = FormBorderStyle.FixedToolWindow,
            StartPosition = FormStartPosition.Manual,
            Location = new Point(-32000, -32000),
            Size = new Size(1, 1),
            Text = "Yi He Lee Dispatcher"
        };
        _ = _dispatcherForm.Handle;
        _userInteraction.AttachDispatcher(_dispatcherForm);
        _userInteraction.ExcelSafetyConfirmationHandler = ConfirmExcelSafetyAsync;

        _statusItem = new ToolStripMenuItem("狀態：初始化中") { Enabled = false };
        // 「立即執行」語意已不清楚：改為兩個明確操作，避免使用者混淆盤中判斷與收盤更新。
        _runIntradayItem = new ToolStripMenuItem("立即執行盤中判斷", null, async (_, _) => await RunIntradayNowAsync());
        _runCloseItem = new ToolStripMenuItem("立即執行收盤更新", null, async (_, _) => await RunCloseNowAsync(null));
        _settingsItem = new ToolStripMenuItem("設定", null, async (_, _) =>
        {
            await ShowMainWindowAsync();
            _mainForm!.ShowSettingsTab();
        });
        _openExcelItem = new ToolStripMenuItem("開啟設定的 Excel", null, async (_, _) => await OpenConfiguredExcelAsync());
        // 是否顯示交由 AppSettings.ShowHistoricalPriceButton 這個 config 旗標控制，
        // 這裡先預設隱藏，待下方非同步載入設定後再依實際值同步顯示狀態。
        _historicalPriceItem = new ToolStripMenuItem("歷史收盤價", null, (_, _) => ShowHistoricalPriceForm())
        {
            Visible = false
        };
        _administratorItem = new ToolStripMenuItem();
        ConfigureAdministratorMenu();

        var menu = new ContextMenuStrip();
        menu.Items.AddRange([
            _statusItem,
            new ToolStripSeparator(),
            _runIntradayItem,
            _runCloseItem,
            _settingsItem,
            _openExcelItem,
            _historicalPriceItem,
            new ToolStripSeparator(),
            new ToolStripMenuItem("開啟資料資料夾", null, (_, _) => OpenFolder(_paths.RootDirectory)),
            new ToolStripMenuItem("開啟 Log 資料夾", null, (_, _) => OpenFolder(_paths.LogDirectory)),
            _administratorItem,
            new ToolStripSeparator(),
            new ToolStripMenuItem("結束", null, async (_, _) => await ExitApplicationAsync())
        ]);

        _notifyIcon = new NotifyIcon
        {
            Icon = AdministratorHelper.IsAdministrator() ? SystemIcons.Shield : SystemIcons.Application,
            Text = "Yi He Lee－初始化中",
            Visible = true,
            ContextMenuStrip = menu
        };
        _notifyIcon.DoubleClick += async (_, _) => await ShowMainWindowAsync();

        _userInteraction.StatusChanged += SetStatus;
        _userInteraction.ProgressDetailChanged += SetProgressDetail;
        _userInteraction.Succeeded += ShowSuccessCenter;
        _userInteraction.Failed += ShowFailureCenter;
        _intradayMonitoringService.RunCompleted += OnIntradayRunCompleted;
        _scheduleCoordinator.StatusChanged += OnWorkflowStatusChanged;

        var forceSettings = args.Any(x => string.Equals(x, "--settings", StringComparison.OrdinalIgnoreCase));
        _dispatcherForm.BeginInvoke(new Action(async () =>
        {
            var settings = await _settingsStore.LoadAsync(_lifetimeCts.Token);
            // 依 config 旗標同步系統匣選單「歷史收盤價」項目的顯示狀態。
            _historicalPriceItem.Visible = settings.ShowHistoricalPriceButton;

            // 排程一律啟動；盤中監控與收盤更新是否實際執行由排程器依設定與時段決定。
            await _scheduleCoordinator.StartAsync();
            if (settings.EnableIntradayMonitoring || settings.EnableDailySchedule)
            {
                SetStatus("排程執行中：交易日 09:00～13:30 盤中每分鐘判斷，13:35 收盤更新", 0);
            }
            else
            {
                SetStatus("盤中監控與收盤更新排程皆已停用，可由選單手動執行", 0);
            }
            if (forceSettings || string.IsNullOrWhiteSpace(settings.WorkbookPath))
            {
                await ShowMainWindowAsync();
                _mainForm!.ShowSettingsTab();
            }
            else if (!args.Any(x => string.Equals(x, "--minimized", StringComparison.OrdinalIgnoreCase)))
            {
                ShowTrayBalloon("Yi He Lee 已啟動",
                    "程式已常駐右下角。交易日 09:00～13:30 盤中每分鐘判斷（使用上一交易日均價），13:35 收盤更新。",
                    ToolTipIcon.Info);
            }
        }));
    }

    private void ConfigureAdministratorMenu()
    {
        // 程式不需要、也不應該以系統管理員權限執行（需與一般權限開啟的 Excel 搭配）。
        // 這裡只顯示目前狀態供使用者確認，不提供提升權限的選項。
        _administratorItem.Text = AdministratorHelper.IsAdministrator()
            ? "警告：目前以系統管理員執行，請改用一般權限重新啟動"
            : "目前以一般權限執行（正確）";
        _administratorItem.Enabled = false;
    }

    /// <summary>手動「立即執行盤中判斷」：只使用上一交易日已保存均價，不抓任何官方資料、不寫 Excel。</summary>
    private async Task RunIntradayNowAsync()
    {
        if (_isExiting)
        {
            return;
        }

        if (_manualRunTask is { IsCompleted: false } || _closePrecomputeJobService.IsRunning)
        {
            MessageBox.Show("目前已有工作正在執行。", "Yi He Lee", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        SetManualButtonsEnabled(false);
        _manualRunTask = RunIntradayCoreAsync();
        try
        {
            await _manualRunTask;
        }
        finally
        {
            if (!_isExiting)
            {
                SetManualButtonsEnabled(true);
            }
        }
    }

    private async Task RunIntradayCoreAsync()
    {
        try
        {
            await _intradayMonitoringService.RunOnceAsync(isManualRun: true, _clock.GetTaipeiNow(), _lifetimeCts.Token);
        }
        catch (OperationCanceledException) when (_lifetimeCts.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            _logger.Error("手動執行盤中判斷發生未處理錯誤。", ex);
            SetStatus("手動盤中判斷失敗", 0);
            MessageBox.Show(ex.Message, "Yi He Lee－盤中判斷失敗", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    /// <summary>手動「立即執行收盤更新」：官方收盤價與均價前置更新；指定過去日期仍屬收盤／歷史資料流程，
    /// 不啟動盤中客戶判斷，且仍遵守官方來源日期規則。</summary>
    private async Task RunCloseNowAsync(DateOnly? manualTargetDate)
    {
        if (_isExiting)
        {
            return;
        }

        if (_manualRunTask is { IsCompleted: false } || _closePrecomputeJobService.IsRunning)
        {
            MessageBox.Show("目前已有工作正在執行。", "Yi He Lee", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        SetManualButtonsEnabled(false);
        _manualRunTask = RunCloseCoreAsync(manualTargetDate);
        try
        {
            await _manualRunTask;
        }
        finally
        {
            if (!_isExiting)
            {
                SetManualButtonsEnabled(true);
            }
        }
    }

    private async Task RunCloseCoreAsync(DateOnly? manualTargetDate)
    {
        try
        {
            await _closePrecomputeJobService.RunAsync(isManualRun: true, _lifetimeCts.Token, manualTargetDate);
        }
        catch (OperationCanceledException) when (_lifetimeCts.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            _logger.Error("手動執行收盤更新發生未處理錯誤。", ex);
            SetStatus("手動收盤更新失敗", 0);
            MessageBox.Show(ex.Message, "Yi He Lee－收盤更新失敗", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    private void SetManualButtonsEnabled(bool enabled)
    {
        _runIntradayItem.Enabled = enabled;
        _runCloseItem.Enabled = enabled;
    }

    /// <summary>盤中判斷完成：中央結果畫面持續更新目前成立清單，但只有新觸發或重要錯誤才主動彈出。</summary>
    private void OnIntradayRunCompleted(IntradayRunSummary summary)
    {
        if (_isExiting || _dispatcherForm.IsDisposed)
        {
            return;
        }

        try
        {
            _dispatcherForm.BeginInvoke(new Action(async () =>
            {
                var jobSummary = ToJobRunSummary(summary);
                if (summary.NewNotificationCount > 0)
                {
                    ShowTrayBalloon("Yi He Lee 盤中新通知",
                        $"新觸發 {summary.NewNotificationCount} 筆（基準 {summary.BaselineTradeDate:yyyy-MM-dd}）。", ToolTipIcon.Info);
                    await ShowMainWindowAsync();
                    _mainForm!.SwitchToResultsTab(jobSummary, isSuccess: true);
                }
                else if (summary.Status == IntradayRunStatus.Failed)
                {
                    ShowTrayBalloon("Yi He Lee 盤中判斷失敗", summary.Message, ToolTipIcon.Error);
                    await ShowMainWindowAsync();
                    _mainForm!.SwitchToResultsTab(jobSummary, isSuccess: false);
                }
            }));
        }
        catch (ObjectDisposedException)
        {
            // 結束流程中不再更新畫面。
        }
    }

    /// <summary>把盤中彙整結果轉成既有中央結果畫面可顯示的摘要；不寫入 JobRuns，僅供畫面呈現。</summary>
    private static JobRunSummary ToJobRunSummary(IntradayRunSummary summary)
        => new(
            Guid.NewGuid(),
            summary.EvaluationDate,
            summary.Status is IntradayRunStatus.Succeeded or IntradayRunStatus.PartialSuccess
                ? JobStatus.Succeeded
                : JobStatus.CrawlFailed,
            summary.Status == IntradayRunStatus.Failed ? RunOutcome.RetryableFailure : RunOutcome.Success,
            summary.Message,
            0,
            0,
            summary.HoldingCount,
            summary.ActiveTriggerCount,
            summary.MissingMovingAverageCount,
            summary.ScheduledAt,
            summary.EvaluatedAt,
            summary.Alerts,
            []);

    /// <summary>排程狀態變更：更新系統匣文字與主視窗狀態列（依時段顯示，不再只顯示「等待每日 13:35」）。</summary>
    private void OnWorkflowStatusChanged(MarketWorkflowStatusSnapshot snapshot)
    {
        if (_isExiting || _dispatcherForm.IsDisposed)
        {
            return;
        }

        try
        {
            _dispatcherForm.BeginInvoke(new Action(() =>
            {
                _lastWorkflowStatus = snapshot;
                var text = snapshot.StatusText;
                _notifyIcon.Text = "Yi He Lee－" + (text.Length > 45 ? text[..45] : text);
                _statusItem.Text = text.Length > 80 ? $"狀態：{text[..77]}..." : $"狀態：{text}";
                _mainForm?.UpdateWorkflowStatus(snapshot);
            }));
        }
        catch (ObjectDisposedException)
        {
            // 結束流程中不再更新畫面。
        }
    }

    private async Task ShowMainWindowAsync()
    {
        if (_isExiting)
        {
            return;
        }

        if (_mainForm is null || _mainForm.IsDisposed)
        {
            var current = await _settingsStore.LoadAsync(_lifetimeCts.Token);
            _mainForm = new MainForm(
                current,
                _validationService,
                SaveSettingsAsync,
                RunIntradayNowAsync,
                RunCloseNowAsync,
                OpenConfiguredExcelAsync,
                ShowHistoricalPriceForm,
                () => OpenFolder(_paths.LogDirectory));

            // 主視窗可能在狀態已更新後才建立，補上最後一次狀態，避免顯示過時的「初始化中」。
            _mainForm.UpdateStatus(_lastStatusMessage, _lastStatusPercent);
            if (_lastWorkflowStatus is not null)
            {
                _mainForm.UpdateWorkflowStatus(_lastWorkflowStatus);
            }
        }

        _mainForm.ShowAndActivate();
    }

    private async Task<bool> ConfirmExcelSafetyAsync(CancellationToken cancellationToken)
    {
        if (_isExiting)
        {
            return false;
        }

        await ShowMainWindowAsync();
        if (_mainForm is null)
        {
            return false;
        }

        return await _mainForm.ConfirmExcelSafetyAsync(cancellationToken);
    }

    private async Task SaveSettingsAsync(AppSettings settings)
    {
        await _settingsStore.SaveAsync(settings, _lifetimeCts.Token);
        // 設定儲存後即時同步系統匣選單「歷史收盤價」項目的顯示狀態，不需重啟程式。
        _historicalPriceItem.Visible = settings.ShowHistoricalPriceButton;
        try
        {
            _startupManager.SetEnabled(settings.StartWithWindows);
        }
        catch (Exception ex)
        {
            _logger.Error("設定 Windows 開機啟動失敗。", ex);
            MessageBox.Show(
                $"設定已儲存，但開機啟動設定失敗：{ex.Message}",
                "Yi He Lee",
                MessageBoxButtons.OK,
                MessageBoxIcon.Warning);
        }

        SetStatus("設定已儲存，排程將於下一次檢查時套用", 0);
    }

    private async Task OpenConfiguredExcelAsync()
    {
        try
        {
            var settings = await _settingsStore.LoadAsync(_lifetimeCts.Token);
            if (string.IsNullOrWhiteSpace(settings.WorkbookPath) || !File.Exists(settings.WorkbookPath))
            {
                MessageBox.Show("尚未設定有效的 Excel 檔案。", "Yi He Lee", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            Process.Start(new ProcessStartInfo(settings.WorkbookPath) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            _logger.Error("開啟 Excel 失敗。", ex);
            MessageBox.Show(ex.Message, "Yi He Lee", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    private async void ShowSuccessCenter(JobRunSummary summary)
    {
        SetStatus(summary.Message, 100);
        ShowTrayBalloon("Yi He Lee 執行完成", summary.Message, ToolTipIcon.Info);
        await ShowMainWindowAsync();
        _mainForm!.SwitchToResultsTab(summary, isSuccess: true);
    }

    private async void ShowFailureCenter(JobRunSummary summary)
    {
        SetStatus($"失敗：{summary.Message}", 0);
        ShowTrayBalloon("Yi He Lee 執行失敗", summary.Message, ToolTipIcon.Error);
        await ShowMainWindowAsync();
        _mainForm!.SwitchToResultsTab(summary, isSuccess: false);
    }

    private void ShowHistoricalPriceForm()
    {
        if (_isExiting)
        {
            return;
        }

        if (_historicalPriceForm is { IsDisposed: false })
        {
            _historicalPriceForm.BringToFront();
            _historicalPriceForm.Activate();
            return;
        }

        _historicalPriceForm = new HistoricalPriceForm(
            _marketDataRepository,
            _stockHistoryImportService,
            _stockPriceImportRepository,
            _settingsStore,
            _logger,
            _crawlerRegistry,
            _stockPriceValidationService);
        _historicalPriceForm.FormClosed += (_, _) => _historicalPriceForm = null;
        _historicalPriceForm.Show();
    }

    private void SetStatus(string message, int percentComplete)
    {
        var oneLine = message.ReplaceLineEndings(" ").Trim();
        _lastStatusMessage = oneLine;
        _lastStatusPercent = percentComplete;
        _statusItem.Text = oneLine.Length > 80 ? $"狀態：{oneLine[..77]}..." : $"狀態：{oneLine}";
        _notifyIcon.Text = "Yi He Lee－" + (oneLine.Length > 45 ? oneLine[..45] : oneLine);
        _mainForm?.UpdateStatus(oneLine, percentComplete);
    }

    /// <summary>長時間作業（例如 MA120 歷史回補）的逐日細節進度，顯示於操作頁進度條下方。</summary>
    private void SetProgressDetail(string message) => _mainForm?.UpdateProgressDetail(message);

    private void ShowTrayBalloon(string title, string text, ToolTipIcon icon)
    {
        try
        {
            _notifyIcon.BalloonTipTitle = title;
            _notifyIcon.BalloonTipText = text.Length > 240 ? text[..240] : text;
            _notifyIcon.BalloonTipIcon = icon;
            _notifyIcon.ShowBalloonTip(4000);
        }
        catch
        {
            // 系統匣提示失敗不影響中央視窗及主流程。
        }
    }

    private static void OpenFolder(string path)
    {
        Directory.CreateDirectory(path);
        Process.Start(new ProcessStartInfo("explorer.exe", $"\"{path}\"") { UseShellExecute = true });
    }

    private async Task ExitApplicationAsync()
    {
        if (_isExiting)
        {
            return;
        }

        _isExiting = true;
        SetManualButtonsEnabled(false);
        _settingsItem.Enabled = false;
        _openExcelItem.Enabled = false;
        _lifetimeCts.Cancel();

        try
        {
            await _scheduleCoordinator.StopAsync();
            if (_manualRunTask is not null)
            {
                try { await _manualRunTask; } catch { }
            }
        }
        finally
        {
            _mainForm?.AllowRealClose();
            _mainForm?.Close();
            _historicalPriceForm?.Close();
            _notifyIcon.Visible = false;
            _notifyIcon.Dispose();
            _dispatcherForm.Dispose();
            ExitThread();
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _userInteraction.StatusChanged -= SetStatus;
            _userInteraction.ProgressDetailChanged -= SetProgressDetail;
            _userInteraction.Succeeded -= ShowSuccessCenter;
            _userInteraction.Failed -= ShowFailureCenter;
            _intradayMonitoringService.RunCompleted -= OnIntradayRunCompleted;
            _scheduleCoordinator.StatusChanged -= OnWorkflowStatusChanged;
            _lifetimeCts.Dispose();
            try
            {
                _notifyIcon.Visible = false;
                _notifyIcon.Dispose();
            }
            catch (ObjectDisposedException)
            {
                // 結束流程可能已先釋放系統匣圖示。
            }
            _dispatcherForm.Dispose();
        }

        base.Dispose(disposing);
    }
}
