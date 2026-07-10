using System.Diagnostics;
using YiHeLee.App.Forms;
using YiHeLee.App.Infrastructure;
using YiHeLee.Application.Abstractions;
using YiHeLee.Application.Services;
using YiHeLee.Domain;

namespace YiHeLee.App;

/// <summary>常駐 Windows 右下角系統匣，負責 UI 互動；商業流程仍由 Application Service 執行。</summary>
internal sealed class TrayApplicationContext : ApplicationContext
{
    private readonly AppPaths _paths;
    private readonly DailyJobService _dailyJobService;
    private readonly DailyScheduleCoordinator _scheduleCoordinator;
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
    private readonly ToolStripMenuItem _runNowItem;
    private readonly ToolStripMenuItem _settingsItem;
    private readonly ToolStripMenuItem _openExcelItem;
    private readonly ToolStripMenuItem _historicalPriceItem;
    private readonly ToolStripMenuItem _administratorItem;
    private readonly CancellationTokenSource _lifetimeCts = new();
    private Task? _manualRunTask;
    private MainForm? _mainForm;
    private HistoricalPriceForm? _historicalPriceForm;
    private bool _isExiting;

    public TrayApplicationContext(
        string[] args,
        AppPaths paths,
        DailyJobService dailyJobService,
        DailyScheduleCoordinator scheduleCoordinator,
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
        _dailyJobService = dailyJobService;
        _scheduleCoordinator = scheduleCoordinator;
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
        _runNowItem = new ToolStripMenuItem("立即執行", null, async (_, _) => await RunNowAsync());
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
            _runNowItem,
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
            Text = "Yi He Lee－等待每日 13:35",
            Visible = true,
            ContextMenuStrip = menu
        };
        _notifyIcon.DoubleClick += async (_, _) => await ShowMainWindowAsync();

        _userInteraction.StatusChanged += SetStatus;
        _userInteraction.ProgressDetailChanged += SetProgressDetail;
        _userInteraction.Succeeded += ShowSuccessCenter;
        _userInteraction.Failed += ShowFailureCenter;

        _scheduleCoordinator.Start();
        SetStatus("等待每日台北時間 13:35，自動執行中", 0);

        var forceSettings = args.Any(x => string.Equals(x, "--settings", StringComparison.OrdinalIgnoreCase));
        _dispatcherForm.BeginInvoke(new Action(async () =>
        {
            var settings = await _settingsStore.LoadAsync(_lifetimeCts.Token);
            // 依 config 旗標同步系統匣選單「歷史收盤價」項目的顯示狀態。
            _historicalPriceItem.Visible = settings.ShowHistoricalPriceButton;
            if (forceSettings || string.IsNullOrWhiteSpace(settings.WorkbookPath))
            {
                await ShowMainWindowAsync();
                _mainForm!.ShowSettingsTab();
            }
            else if (!args.Any(x => string.Equals(x, "--minimized", StringComparison.OrdinalIgnoreCase)))
            {
                ShowTrayBalloon("Yi He Lee 已啟動", "程式已常駐右下角，將於每日台北時間 13:35 執行。", ToolTipIcon.Info);
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

    private async Task RunNowAsync(DateOnly? manualTargetDate = null)
    {
        if (_isExiting)
        {
            return;
        }

        if (_manualRunTask is { IsCompleted: false } || _dailyJobService.IsRunning)
        {
            MessageBox.Show("目前已有工作正在執行。", "Yi He Lee", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        _runNowItem.Enabled = false;
        _manualRunTask = RunManualCoreAsync(manualTargetDate);
        try
        {
            await _manualRunTask;
        }
        finally
        {
            if (!_isExiting)
            {
                _runNowItem.Enabled = true;
            }
        }
    }

    private async Task RunManualCoreAsync(DateOnly? manualTargetDate)
    {
        try
        {
            await _dailyJobService.RunAsync(isManualRun: true, _lifetimeCts.Token, manualTargetDate);
        }
        catch (OperationCanceledException) when (_lifetimeCts.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            _logger.Error("手動執行發生未處理錯誤。", ex);
            SetStatus("手動執行失敗", 0);
            MessageBox.Show(ex.Message, "Yi He Lee－執行失敗", MessageBoxButtons.OK, MessageBoxIcon.Error);
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
                RunNowAsync,
                OpenConfiguredExcelAsync,
                ShowHistoricalPriceForm,
                () => OpenFolder(_paths.LogDirectory));
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

        SetStatus("設定已儲存，等待每日台北時間 13:35", 0);
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
        _runNowItem.Enabled = false;
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
