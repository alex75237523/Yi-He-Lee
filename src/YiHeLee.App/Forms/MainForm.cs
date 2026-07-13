using YiHeLee.App.Infrastructure;
using YiHeLee.Application.Services;
using YiHeLee.Domain;

namespace YiHeLee.App.Forms;

/// <summary>常駐主視窗：操作／設定／每日結果三個頁籤，取代原本 Modal 設定視窗與獨立結果彈出視窗。</summary>
internal sealed partial class MainForm : Form
{
    private readonly SettingsValidationService _validationService;
    private readonly Func<AppSettings, Task> _onSaveSettings;
    private readonly Func<Task> _runIntradayAction;
    private readonly Func<DateOnly?, Task> _runCloseAction;
    private readonly Func<Task> _openExcelAction;
    private readonly Action _historicalPriceAction;
    private readonly Action _openLogFolderAction;

    private readonly TabControl _tabs;
    private readonly TabPage _operationsTab;
    private readonly TabPage _settingsTab;
    private readonly TabPage _resultsTab;

    private bool _allowClose;

    public MainForm(
        AppSettings initialSettings,
        SettingsValidationService validationService,
        Func<AppSettings, Task> onSaveSettings,
        Func<Task> runIntradayAction,
        Func<DateOnly?, Task> runCloseAction,
        Func<Task> openExcelAction,
        Action historicalPriceAction,
        Action openLogFolderAction)
    {
        _validationService = validationService;
        _onSaveSettings = onSaveSettings;
        _runIntradayAction = runIntradayAction;
        _runCloseAction = runCloseAction;
        _openExcelAction = openExcelAction;
        _historicalPriceAction = historicalPriceAction;
        _openLogFolderAction = openLogFolderAction;

        // 主畫面標題顯示簡短版本與 Git Commit SHA，方便確認目前啟動的不是舊資料夾裡的舊 EXE。
        Text = $"Yi He Lee － {BuildInfo.ShortDescription}";
        StartPosition = FormStartPosition.CenterScreen;
        // 尺寸以 96 DPI 為基準，高 DPI 螢幕下整體等比放大，避免字型放大但欄寬不變造成破版。
        AutoScaleDimensions = new SizeF(96F, 96F);
        AutoScaleMode = AutoScaleMode.Dpi;
        // 視窗大小依「一般設定」頁完整內容計算（兩欄排版約需 1000×740），避免大片留白。
        MinimumSize = new Size(1000, 740);
        Size = new Size(1000, 740);
        Font = new Font("Microsoft JhengHei UI", 10F);
        ApplyAppIcon(initialSettings);

        _tabs = new TabControl { Dock = DockStyle.Fill };
        _operationsTab = BuildOperationsTab(initialSettings.ShowHistoricalPriceButton, initialSettings.ShowStatusText);
        _settingsTab = BuildSettingsTab(initialSettings);
        _resultsTab = new TabPage("每日結果");
        RenderIdlePlaceholder();

        _tabs.TabPages.Add(_operationsTab);
        _tabs.TabPages.Add(_settingsTab);
        _tabs.TabPages.Add(_resultsTab);
        Controls.Add(_tabs);

        // DPI 放大後若超出螢幕工作區，縮回工作區大小，避免視窗超出畫面。
        Load += (_, _) =>
        {
            var workingArea = Screen.FromControl(this).WorkingArea;
            if (Width > workingArea.Width || Height > workingArea.Height)
            {
                MinimumSize = new Size(Math.Min(MinimumSize.Width, workingArea.Width), Math.Min(MinimumSize.Height, workingArea.Height));
                Size = new Size(Math.Min(Width, workingArea.Width), Math.Min(Height, workingArea.Height));
                Location = new Point(
                    workingArea.Left + (workingArea.Width - Width) / 2,
                    workingArea.Top + (workingArea.Height - Height) / 2);
            }
        };

        // 關閉視窗只隱藏，程式仍留在系統匣；只有結束程式時才真正關閉（見 AllowRealClose）。
        FormClosing += (_, e) =>
        {
            if (_allowClose)
            {
                return;
            }

            e.Cancel = true;
            Hide();
        };
    }

    /// <summary>允許這次 Close() 真正關閉視窗，供程式結束流程使用。</summary>
    public void AllowRealClose() => _allowClose = true;

    public void ShowAndActivate()
    {
        if (IsDisposed)
        {
            return;
        }

        if (WindowState == FormWindowState.Minimized)
        {
            WindowState = FormWindowState.Normal;
        }

        Show();
        BringToFront();
        Activate();
    }

    public void ShowOperationsTab() => _tabs.SelectedTab = _operationsTab;

    public void ShowSettingsTab() => _tabs.SelectedTab = _settingsTab;

    public void ApplyAppIcon(AppSettings settings)
    {
        var oldIcon = Icon;
        Icon = AppIcon.Create(settings);
        oldIcon?.Dispose();
    }
}
