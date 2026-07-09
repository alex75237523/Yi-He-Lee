using YiHeLee.Application.Abstractions;
using YiHeLee.Domain;

namespace YiHeLee.Application.Services;

/// <summary>只負責依台北時間觸發工作，不包含爬文、Excel 或資料庫商業邏輯。</summary>
public sealed class DailyScheduleCoordinator : IAsyncDisposable
{
    private readonly DailyJobService _dailyJobService;
    private readonly IClock _clock;
    private readonly ISettingsStore _settingsStore;
    private readonly IYiHeLeeRepository _repository;
    private readonly IAppLogger _logger;
    private CancellationTokenSource? _cts;
    private Task? _loopTask;

    public DailyScheduleCoordinator(
        DailyJobService dailyJobService,
        IClock clock,
        ISettingsStore settingsStore,
        IYiHeLeeRepository repository,
        IAppLogger logger)
    {
        _dailyJobService = dailyJobService;
        _clock = clock;
        _settingsStore = settingsStore;
        _repository = repository;
        _logger = logger;
    }

    public void Start()
    {
        if (_loopTask is not null)
        {
            return;
        }

        _cts = new CancellationTokenSource();
        _loopTask = RunLoopAsync(_cts.Token);
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
    }

    private async Task RunLoopAsync(CancellationToken cancellationToken)
    {
        // 啟動後先立即檢查一次；若程式在 13:35 後才啟動，不必再等下一個整分鐘。
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                await TryRunDueWorkAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.Error("排程觸發器發生未預期錯誤，30 秒後會再次檢查。", ex);
            }

            await Task.Delay(TimeSpan.FromSeconds(30), cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task TryRunDueWorkAsync(CancellationToken cancellationToken)
    {
        var settings = await _settingsStore.LoadAsync(cancellationToken).ConfigureAwait(false);
        var now = _clock.GetTaipeiNow();
        var today = DateOnly.FromDateTime(now.DateTime);
        var scheduledAt = today.ToDateTime(AppSettings.FixedDailyRunTime);

        if (now.DateTime < scheduledAt)
        {
            return;
        }

        var attemptCount = await _repository.GetAttemptCountAsync(today, cancellationToken).ConfigureAwait(false);
        if (attemptCount >= settings.MaximumDailyAttempts)
        {
            return;
        }

        var latest = await _repository.GetLatestJobSummaryAsync(cancellationToken).ConfigureAwait(false);
        if (latest is not null && latest.TargetDate == today && latest.Status == JobStatus.Succeeded)
        {
            return;
        }

        if (latest is not null && latest.TargetDate == today)
        {
            var nextRetryAt = latest.CompletedAt.AddMinutes(settings.RetryIntervalMinutes);
            if (now < nextRetryAt || latest.Outcome == RunOutcome.NonRetryableFailure)
            {
                return;
            }
        }

        await _dailyJobService.RunAsync(isManualRun: false, cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask DisposeAsync() => await StopAsync().ConfigureAwait(false);
}
