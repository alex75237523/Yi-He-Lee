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
    private readonly Form _dispatcherForm;
    private readonly NotifyIcon _notifyIcon;
    private readonly ToolStripMenuItem _statusItem;
    private readonly ToolStripMenuItem _runNowItem;
    private readonly ToolStripMenuItem _settingsItem;
    private readonly ToolStripMenuItem _openExcelItem;
    private readonly ToolStripMenuItem _administratorItem;
    private readonly CancellationTokenSource _lifetimeCts = new();
    private Task? _manualRunTask;
    private ResultCenterForm? _resultForm;
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
        IAppLogger logger)
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

        _statusItem = new ToolStripMenuItem("狀態：初始化中") { Enabled = false };
        _runNowItem = new ToolStripMenuItem("立即執行", null, async (_, _) => await RunNowAsync());
        _settingsItem = new ToolStripMenuItem("設定", null, async (_, _) => await ShowSettingsAsync());
        _openExcelItem = new ToolStripMenuItem("開啟設定的 Excel", null, async (_, _) => await OpenConfiguredExcelAsync());
        _administratorItem = new ToolStripMenuItem();
        ConfigureAdministratorMenu();

        var menu = new ContextMenuStrip();
        menu.Items.AddRange([
            _statusItem,
            new ToolStripSeparator(),
            _runNowItem,
            _settingsItem,
            _openExcelItem,
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
        _notifyIcon.DoubleClick += async (_, _) => await ShowSettingsAsync();

        _userInteraction.StatusChanged += SetStatus;
        _userInteraction.Succeeded += ShowSuccessCenter;
        _userInteraction.Failed += ShowFailureCenter;

        _scheduleCoordinator.Start();
        SetStatus("等待每日台北時間 13:35，自動執行中");

        var forceSettings = args.Any(x => string.Equals(x, "--settings", StringComparison.OrdinalIgnoreCase));
        _dispatcherForm.BeginInvoke(new Action(async () =>
        {
            var settings = await _settingsStore.LoadAsync(_lifetimeCts.Token);
            if (forceSettings || string.IsNullOrWhiteSpace(settings.WorkbookPath))
            {
                await ShowSettingsAsync();
            }
            else if (!args.Any(x => string.Equals(x, "--minimized", StringComparison.OrdinalIgnoreCase)))
            {
                ShowTrayBalloon("Yi He Lee 已啟動", "程式已常駐右下角，將於每日台北時間 13:35 執行。", ToolTipIcon.Info);
            }
        }));
    }

    private void ConfigureAdministratorMenu()
    {
        if (AdministratorHelper.IsAdministrator())
        {
            _administratorItem.Text = "目前以系統管理員執行";
            _administratorItem.Enabled = false;
            return;
        }

        _administratorItem.Text = "以系統管理員重新啟動";
        _administratorItem.Click += async (_, _) =>
        {
            var result = MessageBox.Show(
                "提升權限後，Excel 也必須用相同的系統管理員權限開啟，否則程式可能找不到已開啟的活頁簿。\r\n\r\n是否仍要重新啟動？",
                "Yi He Lee－系統管理員權限",
                MessageBoxButtons.YesNo,
                MessageBoxIcon.Warning,
                MessageBoxDefaultButton.Button2);
            if (result != DialogResult.Yes)
            {
                return;
            }

            try
            {
                AdministratorHelper.RestartAsAdministrator();
                await ExitApplicationAsync();
            }
            catch (Exception ex)
            {
                _logger.Error("以系統管理員重新啟動失敗。", ex);
                MessageBox.Show(ex.Message, "重新啟動失敗", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        };
    }

    private async Task RunNowAsync()
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
        _manualRunTask = RunManualCoreAsync();
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

    private async Task RunManualCoreAsync()
    {
        try
        {
            await _dailyJobService.RunAsync(isManualRun: true, _lifetimeCts.Token);
        }
        catch (OperationCanceledException) when (_lifetimeCts.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            _logger.Error("手動執行發生未處理錯誤。", ex);
            SetStatus("手動執行失敗");
            MessageBox.Show(ex.Message, "Yi He Lee－執行失敗", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    private async Task ShowSettingsAsync()
    {
        if (_isExiting)
        {
            return;
        }

        try
        {
            var current = await _settingsStore.LoadAsync(_lifetimeCts.Token);
            using var form = new SettingsForm(current, _validationService, AdministratorHelper.IsAdministrator());
            if (form.ShowDialog() != DialogResult.OK || form.ResultSettings is null)
            {
                return;
            }

            await _settingsStore.SaveAsync(form.ResultSettings, _lifetimeCts.Token);
            try
            {
                _startupManager.SetEnabled(form.ResultSettings.StartWithWindows);
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

            SetStatus("設定已儲存，等待每日台北時間 13:35");
        }
        catch (OperationCanceledException) when (_lifetimeCts.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            _logger.Error("開啟或儲存設定失敗。", ex);
            MessageBox.Show(ex.Message, "Yi He Lee－設定失敗", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
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

    private void ShowSuccessCenter(JobRunSummary summary)
    {
        SetStatus(summary.Message);
        ShowTrayBalloon("Yi He Lee 執行完成", summary.Message, ToolTipIcon.Info);
        ShowResultForm(ResultCenterForm.CreateSuccess(
            summary,
            retryAction: RunNowAsync,
            openExcelAction: OpenConfiguredExcelAsync));
    }

    private void ShowFailureCenter(JobRunSummary summary)
    {
        SetStatus($"失敗：{summary.Message}");
        ShowTrayBalloon("Yi He Lee 執行失敗", summary.Message, ToolTipIcon.Error);
        ShowResultForm(ResultCenterForm.CreateFailure(
            summary,
            retryAction: RunNowAsync,
            settingsAction: ShowSettingsAsync,
            logFolderAction: () =>
            {
                OpenFolder(_paths.LogDirectory);
                return Task.CompletedTask;
            }));
    }

    private void ShowResultForm(ResultCenterForm form)
    {
        if (_resultForm is not null && !_resultForm.IsDisposed)
        {
            _resultForm.Close();
        }

        _resultForm = form;
        _resultForm.FormClosed += (_, _) => _resultForm = null;
        _resultForm.Show();
        _resultForm.BringToFront();
        _resultForm.Activate();
    }

    private void SetStatus(string message)
    {
        var oneLine = message.ReplaceLineEndings(" ").Trim();
        _statusItem.Text = oneLine.Length > 80 ? $"狀態：{oneLine[..77]}..." : $"狀態：{oneLine}";
        _notifyIcon.Text = "Yi He Lee－" + (oneLine.Length > 45 ? oneLine[..45] : oneLine);
    }

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
            _resultForm?.Close();
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
