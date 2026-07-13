using YiHeLee.Application.Abstractions;
using YiHeLee.Domain;

namespace YiHeLee.Application.Services;

/// <summary>
/// 市場工作流程排程器（2026-07-13 取代原 DailyScheduleCoordinator）。
/// 只負責依台北時間觸發兩條完全獨立的流程：
/// 1. 盤中監控（<see cref="IntradayMonitoringService"/>）：交易日 09:00～13:30 每 1 分鐘一次，對齊整分鐘；
///    程式於盤中啟動時立即執行一次；上一 Tick 未完成時下一 Tick 直接略過，不排隊。
/// 2. 收盤更新（<see cref="ClosePrecomputeJobService"/>）：13:35 執行；13:35 後啟動且今日尚未成功時補跑。
/// 不包含爬文、Excel 或資料庫商業邏輯；非交易日不啟動盤中監控。
/// </summary>
public sealed class MarketWorkflowScheduleCoordinator : IAsyncDisposable
{
    private readonly IntradayMonitoringService _intradayMonitoringService;
    private readonly ClosePrecomputeJobService _closePrecomputeJobService;
    private readonly ITradingDateResolver _tradingDateResolver;
    private readonly IClock _clock;
    private readonly ISettingsStore _settingsStore;
    private readonly IYiHeLeeRepository _repository;
    private readonly IAppLogger _logger;
    private CancellationTokenSource? _cts;
    private Task? _loopTask;
    private Task<IntradayRunSummary>? _currentIntradayTask;
    private DateTimeOffset? _lastIntradayTickMinute;
    private IntradayRunSummary? _lastIntradaySummary;
    private DateOnly? _lastCloseSucceededDate;

    /// <summary>目前工作流程狀態變更（時段切換、盤中判斷完成、收盤更新完成）時觸發，供 UI 顯示。</summary>
    public event Action<MarketWorkflowStatusSnapshot>? StatusChanged;

    public MarketWorkflowScheduleCoordinator(
        IntradayMonitoringService intradayMonitoringService,
        ClosePrecomputeJobService closePrecomputeJobService,
        ITradingDateResolver tradingDateResolver,
        IClock clock,
        ISettingsStore settingsStore,
        IYiHeLeeRepository repository,
        IAppLogger logger)
    {
        _intradayMonitoringService = intradayMonitoringService;
        _closePrecomputeJobService = closePrecomputeJobService;
        _tradingDateResolver = tradingDateResolver;
        _clock = clock;
        _settingsStore = settingsStore;
        _repository = repository;
        _logger = logger;
        _intradayMonitoringService.RunCompleted += OnIntradayRunCompleted;
    }

    public Task StartAsync()
    {
        if (_loopTask is not null)
        {
            return Task.CompletedTask;
        }

        _cts = new CancellationTokenSource();
        _loopTask = RunLoopAsync(_cts.Token);
        return Task.CompletedTask;
    }

    public async Task StopAsync()
    {
        if (_cts is null || _loopTask is null)
        {
            return;
        }

        _cts.Cancel();
        try
        {
            await _loopTask.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // 正常停止。
        }
        finally
        {
            _cts.Dispose();
            _cts = null;
            _loopTask = null;
        }

        // 程式結束時等待仍在執行的盤中判斷收尾，不留下背景工作。
        if (_currentIntradayTask is { IsCompleted: false } running)
        {
            try
            {
                await running.ConfigureAwait(false);
            }
            catch
            {
                // 收尾中的例外已由 IntradayMonitoringService 自行記錄。
            }
        }
    }

    private async Task RunLoopAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                await TickAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.Error("排程觸發器發生未預期錯誤，稍後會再次檢查。", ex);
            }

            var delay = MarketWorkflowPlanner.GetNextWakeDelay(_clock.GetTaipeiNow());
            await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task TickAsync(CancellationToken cancellationToken)
    {
        var settings = await _settingsStore.LoadAsync(cancellationToken).ConfigureAwait(false);
        var now = _clock.GetTaipeiNow();
        var today = DateOnly.FromDateTime(now.DateTime);
        var time = TimeOnly.FromTimeSpan(now.TimeOfDay);

        if (MarketWorkflowPlanner.IsWithinIntradayWindow(time))
        {
            await HandleIntradayWindowAsync(settings, now, today, cancellationToken).ConfigureAwait(false);
            return;
        }

        if (MarketWorkflowPlanner.IsBetweenIntradayEndAndClose(time))
        {
            // 13:30～13:35：不執行盤中判斷，等待 13:35 收盤更新（含程式此時啟動的情況）。
            await PublishStatusAsync(MarketWorkflowPhase.WaitingForClose, today, now, "等待 13:35 收盤更新", cancellationToken).ConfigureAwait(false);
            return;
        }

        if (MarketWorkflowPlanner.IsCloseUpdateDue(time))
        {
            await HandleCloseWindowAsync(settings, now, today, cancellationToken).ConfigureAwait(false);
            return;
        }

        // 開盤前（00:00～09:00）。
        await PublishStatusAsync(MarketWorkflowPhase.OutsideSchedule, today, now, "非交易時段（等待 09:00 盤中監控）", cancellationToken).ConfigureAwait(false);
    }

    private async Task HandleIntradayWindowAsync(AppSettings settings, DateTimeOffset now, DateOnly today, CancellationToken cancellationToken)
    {
        if (!settings.EnableIntradayMonitoring)
        {
            await PublishStatusAsync(MarketWorkflowPhase.Disabled, today, now, "盤中監控已停用（使用者設定），可手動「立即執行盤中判斷」", cancellationToken).ConfigureAwait(false);
            return;
        }

        // 非交易日（週末或官方批次已記錄休市）不啟動盤中監控。
        if (await _tradingDateResolver.IsKnownNonTradingDayAsync(today, cancellationToken).ConfigureAwait(false))
        {
            await PublishStatusAsync(MarketWorkflowPhase.NonTradingDay, today, now, "非交易日，盤中監控不啟動", cancellationToken).ConfigureAwait(false);
            return;
        }

        var previousStillRunning = _currentIntradayTask is { IsCompleted: false };
        if (!MarketWorkflowPlanner.ShouldTriggerIntradayTick(now, _lastIntradayTickMinute, previousStillRunning))
        {
            if (previousStillRunning && _lastIntradayTickMinute is DateTimeOffset last
                && MarketWorkflowPlanner.TruncateToMinute(now) > MarketWorkflowPlanner.TruncateToMinute(last))
            {
                // 上一次盤中判斷超過 1 分鐘尚未完成：本分鐘直接略過並記住已消耗，不排隊累積。
                _lastIntradayTickMinute = MarketWorkflowPlanner.TruncateToMinute(now);
                _logger.Warning($"盤中判斷已超過 1 分鐘尚未完成，{now:HH:mm} 這一分鐘的 Tick 直接略過，不排隊。");
            }

            return;
        }

        _lastIntradayTickMinute = MarketWorkflowPlanner.TruncateToMinute(now);

        // 不 await：Tick 在背景執行，避免排程迴圈被單次判斷卡住；
        // 併發安全由 IWorkflowExecutionGate 保證（同一時間只有一個盤中判斷）。
        _currentIntradayTask = _intradayMonitoringService.RunOnceAsync(isManualRun: false, now, cancellationToken);
        _ = _currentIntradayTask.ContinueWith(
            t => _logger.Error("盤中判斷背景工作發生未處理錯誤。", t.Exception),
            CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted,
            TaskScheduler.Default);
    }

    private async Task HandleCloseWindowAsync(AppSettings settings, DateTimeOffset now, DateOnly today, CancellationToken cancellationToken)
    {
        if (!settings.EnableDailySchedule)
        {
            await PublishStatusAsync(MarketWorkflowPhase.Disabled, today, now, "收盤更新排程已停用（使用者設定），可手動「立即執行收盤更新」", cancellationToken).ConfigureAwait(false);
            return;
        }

        // 必須查「今日」的最新一筆，不得查整體最新一筆：使用者手動回溯執行過去日期後，
        // 整體最新一筆會變成過去日期，會被誤判為「今日尚未執行」而重跑今日。
        var latest = await _repository.GetLatestJobSummaryForDateAsync(today, cancellationToken).ConfigureAwait(false);
        if (latest is { Status: JobStatus.Succeeded })
        {
            _lastCloseSucceededDate = today;
            await PublishStatusAsync(MarketWorkflowPhase.CloseCompleted, today, now, "今日收盤更新完成", cancellationToken).ConfigureAwait(false);
            return;
        }

        var attemptCount = await _repository.GetAttemptCountAsync(today, cancellationToken).ConfigureAwait(false);
        if (!MarketWorkflowPlanner.ShouldRunCloseUpdate(now, latest, attemptCount, settings.MaximumDailyAttempts, settings.RetryIntervalMinutes))
        {
            await PublishStatusAsync(MarketWorkflowPhase.WaitingForClose, today, now, "等待收盤更新重試或已達當日上限", cancellationToken).ConfigureAwait(false);
            return;
        }

        var summary = await _closePrecomputeJobService.RunAsync(isManualRun: false, cancellationToken).ConfigureAwait(false);
        if (summary.Status == JobStatus.Succeeded)
        {
            _lastCloseSucceededDate = today;
        }

        await PublishStatusAsync(
            summary.Status == JobStatus.Succeeded ? MarketWorkflowPhase.CloseCompleted : MarketWorkflowPhase.WaitingForClose,
            today, _clock.GetTaipeiNow(),
            summary.Status == JobStatus.Succeeded ? "今日收盤更新完成" : $"收盤更新未成功：{summary.Message}",
            cancellationToken).ConfigureAwait(false);
    }

    private void OnIntradayRunCompleted(IntradayRunSummary summary)
    {
        _lastIntradaySummary = summary;
        try
        {
            var now = _clock.GetTaipeiNow();
            var phase = summary.Status == IntradayRunStatus.BaselineNotReady
                ? MarketWorkflowPhase.BaselineNotReady
                : MarketWorkflowPhase.IntradayMonitoring;
            var statusText = summary.Status == IntradayRunStatus.BaselineNotReady
                ? "基準均價資料未就緒"
                : summary.BaselineTradeDate is DateOnly baseline
                    ? $"盤中監控中－基準 {baseline:yyyy-MM-dd}"
                    : "盤中監控中";
            StatusChanged?.Invoke(BuildSnapshot(phase, summary.EvaluationDate, now, statusText));
        }
        catch (Exception ex)
        {
            _logger.Error("更新盤中狀態顯示失敗。", ex);
        }
    }

    private async Task PublishStatusAsync(MarketWorkflowPhase phase, DateOnly today, DateTimeOffset now, string statusText, CancellationToken cancellationToken)
    {
        if (_lastCloseSucceededDate is null)
        {
            // 啟動後補查最後收盤成功日期，供畫面顯示；查詢失敗不影響排程。
            try
            {
                var latestToday = await _repository.GetLatestJobSummaryForDateAsync(today, cancellationToken).ConfigureAwait(false);
                if (latestToday is { Status: JobStatus.Succeeded })
                {
                    _lastCloseSucceededDate = today;
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.Error("查詢今日收盤更新狀態失敗。", ex);
            }
        }

        StatusChanged?.Invoke(BuildSnapshot(phase, today, now, statusText));
    }

    private MarketWorkflowStatusSnapshot BuildSnapshot(MarketWorkflowPhase phase, DateOnly evaluationDate, DateTimeOffset now, string statusText)
    {
        var time = TimeOnly.FromTimeSpan(now.TimeOfDay);
        DateTimeOffset? nextIntradayTickAt = null;
        if (phase is MarketWorkflowPhase.IntradayMonitoring or MarketWorkflowPhase.BaselineNotReady
            && MarketWorkflowPlanner.IsWithinIntradayWindow(time))
        {
            nextIntradayTickAt = MarketWorkflowPlanner.GetNextAlignedMinute(now);
        }

        DateTimeOffset? nextCloseRunAt = null;
        if (time < AppSettings.FixedDailyRunTime)
        {
            nextCloseRunAt = new DateTimeOffset(evaluationDate.ToDateTime(AppSettings.FixedDailyRunTime), now.Offset);
        }
        else if (_lastCloseSucceededDate != evaluationDate)
        {
            nextCloseRunAt = now; // 已過 13:35 且今日未成功：排程會盡快補跑。
        }

        var summary = _lastIntradaySummary;
        return new MarketWorkflowStatusSnapshot(
            phase,
            evaluationDate,
            summary?.BaselineTradeDate,
            summary?.EvaluatedAt,
            nextIntradayTickAt,
            _lastCloseSucceededDate,
            nextCloseRunAt,
            summary?.HoldingCount ?? 0,
            summary?.ActiveTriggerCount ?? 0,
            summary?.NewNotificationCount ?? 0,
            summary?.EntryAveragePriceInvalidCount ?? 0,
            summary?.CurrentPriceInvalidCount ?? 0,
            statusText);
    }

    public async ValueTask DisposeAsync()
    {
        _intradayMonitoringService.RunCompleted -= OnIntradayRunCompleted;
        await StopAsync().ConfigureAwait(false);
    }
}
