using YiHeLee.Application.Abstractions;
using YiHeLee.Domain;

namespace YiHeLee.Application.Services;

/// <summary>
/// 盤中客戶價格自動監控（2026-07-13 盤中／收盤流程拆分新增）。
/// 只負責：解析上一交易日基準、讀取已保存於 SQLite 的上一交易日均價、讀取 Excel 客戶持股
/// 與最新 DDE 現價（唯讀，不備份、不儲存、不覆寫活頁簿）、呼叫既有
/// <see cref="StrategyEvaluationService"/>、以 IntradayAlertState 去重通知、保存盤中執行摘要。
/// 基準缺漏時會透過 <see cref="IBaselinePreparationService"/> 補齊上一交易日官方收盤價並重算均價；
/// 準備成功後必須在同一次呼叫內繼續讀取 Excel 並判斷。
/// 禁止包含：鉅亨擷取、Excel「每日五日均價策略」頁籤寫入；
/// 當天收盤後產生的新均價不得在同一天盤中使用：基準一律由 <see cref="ITradingDateResolver"/> 解析為
/// 嚴格早於 EvaluationDate 的上一交易日。
/// </summary>
public sealed class IntradayMonitoringService
{
    private readonly IClock _clock;
    private readonly ISettingsStore _settingsStore;
    private readonly ITradingDateResolver _tradingDateResolver;
    private readonly IMarketDataRepository _marketDataRepository;
    private readonly IExcelWorkbookService _excelWorkbookService;
    private readonly StockIdentityResolutionService _stockIdentityResolutionService;
    private readonly StrategyEvaluationService _strategyEvaluationService;
    private readonly IIntradayStateRepository _intradayStateRepository;
    private readonly IWorkflowExecutionGate _executionGate;
    private readonly IBaselinePreparationService? _baselinePreparationService;
    private readonly IUserInteraction? _userInteraction;
    private readonly IAppLogger _logger;

    /// <summary>每次盤中判斷完成（含略過、失敗、基準未就緒）後觸發，供中央結果畫面與系統匣更新。</summary>
    public event Action<IntradayRunSummary>? RunCompleted;

    public IntradayMonitoringService(
        IClock clock,
        ISettingsStore settingsStore,
        ITradingDateResolver tradingDateResolver,
        IMarketDataRepository marketDataRepository,
        IExcelWorkbookService excelWorkbookService,
        StockIdentityResolutionService stockIdentityResolutionService,
        StrategyEvaluationService strategyEvaluationService,
        IIntradayStateRepository intradayStateRepository,
        IWorkflowExecutionGate executionGate,
        IAppLogger logger,
        IBaselinePreparationService? baselinePreparationService = null,
        IUserInteraction? userInteraction = null)
    {
        _clock = clock;
        _settingsStore = settingsStore;
        _tradingDateResolver = tradingDateResolver;
        _marketDataRepository = marketDataRepository;
        _excelWorkbookService = excelWorkbookService;
        _stockIdentityResolutionService = stockIdentityResolutionService;
        _strategyEvaluationService = strategyEvaluationService;
        _intradayStateRepository = intradayStateRepository;
        _executionGate = executionGate;
        _logger = logger;
        _baselinePreparationService = baselinePreparationService;
        _userInteraction = userInteraction;
    }

    /// <summary>
    /// 執行一次盤中判斷。<paramref name="scheduledAt"/> 為本次 Tick 的預定時間（排程依 IntradayCheckIntervalSeconds 觸發；
    /// 手動執行為觸發當下）。鎖已被占用（上一次盤中判斷尚未完成或收盤更新執行中）時直接記錄略過，
    /// 不排隊、不同時執行兩次盤中判斷。
    /// </summary>
    public async Task<IntradayRunSummary> RunOnceAsync(bool isManualRun, DateTimeOffset scheduledAt, CancellationToken cancellationToken)
    {
        var evaluationDate = _clock.GetTaipeiToday();

        var ticket = _executionGate.TryEnter("盤中監控：正在準備上一交易日資料或判斷");
        if (ticket is null)
        {
            var owner = _executionGate.CurrentOwner ?? "另一項工作";
            var skipped = CreateSummary(
                evaluationDate, null, scheduledAt, _clock.GetTaipeiNow(), IntradayRunStatus.Skipped,
                $"本次盤中判斷已略過：{owner}尚未完成，依規定不排隊、不同時執行。", isManualRun, 0, 0, 0, 0, 0, 0, [], []);
            await SaveRunAsync(skipped, startedAt: null, skippedReason: skipped.Message, errorMessage: null, cancellationToken).ConfigureAwait(false);
            _logger.Info(skipped.Message);
            PublishFinalStatus(skipped);
            RunCompleted?.Invoke(skipped);
            return skipped;
        }

        using (ticket)
        {
            var startedAt = _clock.GetTaipeiNow();
            IntradayRunSummary summary;
            try
            {
                ShowStatus("盤中判斷：開始執行", 5);
                summary = await RunCoreAsync(isManualRun, evaluationDate, scheduledAt, startedAt, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                // 單次盤中判斷失敗（例如 Excel 活頁簿完全無法存取）只影響本次 Tick，
                // 不影響下一分鐘，也絕不因此啟動官方資料抓取或使用前一次現價。
                summary = CreateSummary(
                    evaluationDate, null, scheduledAt, _clock.GetTaipeiNow(), IntradayRunStatus.Failed,
                    $"盤中判斷失敗（EvaluationDate={evaluationDate:yyyy-MM-dd}）：{ex.Message}", isManualRun, 0, 0, 0, 0, 0, 0, [], []);
                await SaveRunAsync(summary, startedAt, skippedReason: null, errorMessage: ex.Message, cancellationToken).ConfigureAwait(false);
                _logger.Error(summary.Message, ex);
            }

            PublishFinalStatus(summary);
            RunCompleted?.Invoke(summary);
            return summary;
        }
    }

    private async Task<IntradayRunSummary> RunCoreAsync(
        bool isManualRun,
        DateOnly evaluationDate,
        DateTimeOffset scheduledAt,
        DateTimeOffset startedAt,
        CancellationToken cancellationToken)
    {
        var settings = await _settingsStore.LoadAsync(cancellationToken).ConfigureAwait(false);
        ShowStatus("盤中判斷：讀取設定", 8);
        if (string.IsNullOrWhiteSpace(settings.WorkbookPath))
        {
            var invalid = CreateSummary(
                evaluationDate, null, scheduledAt, _clock.GetTaipeiNow(), IntradayRunStatus.Failed,
                "尚未設定 Excel 活頁簿路徑，無法執行盤中判斷。", isManualRun, 0, 0, 0, 0, 0, 0, [], []);
            await SaveRunAsync(invalid, startedAt, null, invalid.Message, cancellationToken).ConfigureAwait(false);
            _logger.Warning(invalid.Message);
            return invalid;
        }

        // 步驟一：解析上一交易日基準。禁止 today-1；快照不完整時先嘗試自動準備，成功後同次繼續判斷。
        ShowStatus("盤中判斷：解析上一交易日均價基準", 12);
        var baseline = await _tradingDateResolver.ResolveBaselineAsync(evaluationDate, cancellationToken).ConfigureAwait(false);
        if (!baseline.IsReady || baseline.BaselineTradeDate is not DateOnly baselineTradeDate)
        {
            if (_baselinePreparationService is null)
            {
                var notReady = CreateSummary(
                    evaluationDate, null, scheduledAt, _clock.GetTaipeiNow(), IntradayRunStatus.BaselineNotReady,
                    $"基準均價資料尚未就緒（EvaluationDate={evaluationDate:yyyy-MM-dd}）：{baseline.NotReadyReason}",
                    isManualRun, 0, 0, 0, 0, 0, 0, [], []);
                await SaveRunAsync(notReady, startedAt, null, baseline.NotReadyReason, cancellationToken).ConfigureAwait(false);
                _logger.Warning(notReady.Message);
                return notReady;
            }

            var preparation = await _baselinePreparationService
                .EnsureBaselineDataAsync(evaluationDate, baseline, settings.OfficialMarketData, cancellationToken).ConfigureAwait(false);
            if (preparation.IsAnotherPreparationRunning || preparation.State.Status is BaselinePreparationStatus.Failed or BaselinePreparationStatus.Partial)
            {
                var waiting = CreateSummary(
                    evaluationDate, preparation.State.BaselineTradeDate, scheduledAt, _clock.GetTaipeiNow(), IntradayRunStatus.BaselineNotReady,
                    preparation.Message,
                    isManualRun, 0, 0, 0, 0, 0, 0, [], []);
                await SaveRunAsync(waiting, startedAt, null, preparation.Message, cancellationToken).ConfigureAwait(false);
                _logger.Warning(waiting.Message);
                return waiting;
            }

            // 關鍵：回補／均價計算後重新呼叫 TradingDateResolver 驗證基準，驗證成功後不得直接結束。
            baseline = await _tradingDateResolver.ResolveBaselineAsync(evaluationDate, cancellationToken).ConfigureAwait(false);
            if (!baseline.IsReady || baseline.BaselineTradeDate is not DateOnly resolvedBaselineTradeDate)
            {
                var notReadyAfterPreparation = CreateSummary(
                    evaluationDate, preparation.State.BaselineTradeDate, scheduledAt, _clock.GetTaipeiNow(), IntradayRunStatus.BaselineNotReady,
                    $"基準資料準備後仍未就緒（EvaluationDate={evaluationDate:yyyy-MM-dd}）：{baseline.NotReadyReason ?? preparation.Message}",
                    isManualRun, 0, 0, 0, 0, 0, 0, [], []);
                await SaveRunAsync(notReadyAfterPreparation, startedAt, null, notReadyAfterPreparation.Message, cancellationToken).ConfigureAwait(false);
                _logger.Warning(notReadyAfterPreparation.Message);
                return notReadyAfterPreparation;
            }

            baselineTradeDate = resolvedBaselineTradeDate;
        }

        // 步驟二：讀取上一交易日已保存於 SQLite 的均價快照（只讀完整提交的資料，不重新計算）。
        ShowStatus($"盤中判斷：讀取 {baselineTradeDate:yyyy-MM-dd} 已保存均價", 45);
        var movingAverages = await _marketDataRepository
            .GetMovingAverageResultsAsync(baselineTradeDate, cancellationToken).ConfigureAwait(false);
        if (movingAverages.Count == 0)
        {
            if (_baselinePreparationService is not null)
            {
                var preparation = await _baselinePreparationService
                    .EnsureBaselineDataAsync(evaluationDate, baseline, settings.OfficialMarketData, cancellationToken).ConfigureAwait(false);
                if (!preparation.IsAnotherPreparationRunning && preparation.State.Status == BaselinePreparationStatus.Ready)
                {
                    baseline = await _tradingDateResolver.ResolveBaselineAsync(evaluationDate, cancellationToken).ConfigureAwait(false);
                    if (baseline.IsReady && baseline.BaselineTradeDate is DateOnly resolved)
                    {
                        baselineTradeDate = resolved;
                        movingAverages = await _marketDataRepository
                            .GetMovingAverageResultsAsync(baselineTradeDate, cancellationToken).ConfigureAwait(false);
                    }
                }
            }

            if (movingAverages.Count == 0)
            {
                var emptySnapshot = CreateSummary(
                    evaluationDate, baselineTradeDate, scheduledAt, _clock.GetTaipeiNow(), IntradayRunStatus.BaselineNotReady,
                    $"基準均價資料尚未就緒：上一交易日 {baselineTradeDate:yyyy-MM-dd} 的均價快照沒有任何資料列。",
                    isManualRun, 0, 0, 0, 0, 0, 0, [], []);
                await SaveRunAsync(emptySnapshot, startedAt, null, emptySnapshot.Message, cancellationToken).ConfigureAwait(false);
                _logger.Warning(emptySnapshot.Message);
                return emptySnapshot;
            }
        }

        // 步驟三：讀取 Excel 客戶持股與最新 DDE 現價（唯讀；單一儲存格 DDE 異常只影響該持股）。
        ShowStatus("盤中判斷：讀取 Excel 客戶持股與最新現價", 60);
        var holdings = await _excelWorkbookService
            .ReadHoldingsAsync(settings, evaluationDate, cancellationToken, detail => ShowProgressDetail(detail)).ConfigureAwait(false);

        // 步驟四：股票代碼正規化與身分解析（重用既有服務，只查 DB 主檔，不觸發任何官方來源）。
        ShowStatus("盤中判斷：正規化股票代碼與市場別", 72);
        var rawHoldingCodes = holdings.Select(x => x.StockCode).ToArray();
        var resolutions = await _stockIdentityResolutionService.ResolveAsync(rawHoldingCodes, cancellationToken).ConfigureAwait(false);
        var holdingStockCodes = resolutions.Values
            .Where(x => x.IsRecognized && x.Identity.IsEligibleForMovingAverageStrategy)
            .Select(x => x.ResolvedCode)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var marketTypes = await _marketDataRepository.GetStockMarketTypesAsync(holdingStockCodes, cancellationToken).ConfigureAwait(false);

        // 步驟五：呼叫既有策略判斷（比較公式不屬於本流程拆分，維持原樣）。
        // StrategyAlert.TradeDate 保存 EvaluationDate（盤中判斷日）；均價本身來自 baselineTradeDate 的快照，
        // 兩個日期在通知文字、Log 與 IntradayEvaluationRun 中分開呈現。
        ShowStatus("盤中判斷：比對均價條件與價格異常", 82);
        var evaluatedAt = _clock.GetTaipeiNow();
        var alerts = _strategyEvaluationService.Evaluate(
            evaluationDate, holdings, movingAverages, marketTypes, evaluatedAt, resolutions);

        // 步驟六：通知去重。從 SQLite 恢復既有狀態（含程式重啟情境），只對「不成立→成立」的新觸發通知。
        ShowStatus("盤中判斷：更新通知去重狀態", 92);
        var previousStates = await _intradayStateRepository
            .GetAlertStatesAsync(evaluationDate, settings.WorkbookPath, cancellationToken).ConfigureAwait(false);
        var (statesToPersist, newlyTriggeredKeys) = ApplyNotificationDeduplication(
            previousStates, alerts, evaluationDate, baselineTradeDate, settings.WorkbookPath, evaluatedAt);
        await _intradayStateRepository.UpsertAlertStatesAsync(statesToPersist, cancellationToken).ConfigureAwait(false);

        var newlyTriggeredAlerts = alerts
            .Where(alert => AlertStateKeysOf(alert).Any(newlyTriggeredKeys.Contains))
            .ToArray();

        var triggeredCount = alerts.Count(x => x.AlertKind == AlertKind.MovingAverageTriggered);
        var entryInvalidCount = alerts.Count(x => x.AlertKind == AlertKind.EntryAveragePriceInvalid);
        var currentInvalidCount = alerts.Count(x => x.AlertKind == AlertKind.CurrentPriceInvalid);
        var missingCount = alerts.Count(x => x.AlertKind == AlertKind.TechnicalIndicatorMissing);
        var status = entryInvalidCount > 0 || currentInvalidCount > 0 || missingCount > 0
            ? IntradayRunStatus.PartialSuccess
            : IntradayRunStatus.Succeeded;

        var baselineState = _baselinePreparationService is null
            ? null
            : await _baselinePreparationService.GetStateAsync(evaluationDate, baselineTradeDate, cancellationToken).ConfigureAwait(false);
        var baselineText = baselineState?.Status == BaselinePreparationStatus.Ready
            ? $"基準資料：沿用 {baselineTradeDate:yyyy-MM-dd} 已完成均價，本輪只重新讀取客戶價格。"
            : "基準資料：已就緒。";

        var message =
            $"盤中判斷完成：EvaluationDate={evaluationDate:yyyy-MM-dd}、BaselineTradeDate={baselineTradeDate:yyyy-MM-dd}、" +
            $"EvaluatedAt={evaluatedAt:yyyy-MM-dd HH:mm:ss zzz}。持股 {holdings.Count} 筆、目前成立 {triggeredCount} 筆、" +
            $"新通知 {newlyTriggeredAlerts.Length} 筆、進場價/平均價異常 {entryInvalidCount} 筆、現價 DDE 異常 {currentInvalidCount} 筆、" +
            $"缺基準均價 {missingCount} 筆。{baselineText}";

        var summary = CreateSummary(
            evaluationDate, baselineTradeDate, scheduledAt, evaluatedAt, status, message,
            isManualRun, holdings.Count, triggeredCount, newlyTriggeredAlerts.Length,
            entryInvalidCount, currentInvalidCount, missingCount, alerts, newlyTriggeredAlerts);
        await SaveRunAsync(summary, startedAt, null, null, cancellationToken).ConfigureAwait(false);
        _logger.Info(summary.Message);
        return summary;
    }

    /// <summary>
    /// 通知去重核心：
    /// 1. 上一次不成立、本次成立 → 新通知（保存 LastNotifiedAt）。
    /// 2. 上一次已成立、本次仍成立 → 只更新 LastEvaluatedAt，不重複通知。
    /// 3. 上一次成立、本次不成立 → IsActive=false、保存 ClearedAt。
    /// 4. 清除後再次成立 → 可以再次通知。
    /// 2026-07-19 起均線觸發改為「進場價/平均價 &gt; MA20 且 現價 &lt; MA5」單一不可拆開的複合條件，
    /// 去重狀態一律 MaWindow=0（不再依 MA 天數 5／20／120 分開跳通知）；價格異常與缺技術資料類通知同樣為 MaWindow=0。
    /// 舊版當日殘留的 MaWindow=5／20／120 Active 狀態，因不在本次的 currentKeys 中，會被正常轉為不成立／清除，
    /// 不會阻擋新的 MaWindow=0 複合通知。不同客戶頁籤、不同列的狀態互相獨立，不得互相覆蓋。
    /// </summary>
    internal static (IReadOnlyList<IntradayAlertStateRecord> StatesToPersist, IReadOnlySet<string> NewlyTriggeredKeys)
        ApplyNotificationDeduplication(
            IReadOnlyList<IntradayAlertStateRecord> previousStates,
            IReadOnlyList<StrategyAlert> alerts,
            DateOnly evaluationDate,
            DateOnly baselineTradeDate,
            string workbookPath,
            DateTimeOffset evaluatedAt)
    {
        var previousByKey = previousStates.ToDictionary(StateKeyOf, StringComparer.Ordinal);
        var statesToPersist = new List<IntradayAlertStateRecord>();
        var newlyTriggeredKeys = new HashSet<string>(StringComparer.Ordinal);
        var currentKeys = new HashSet<string>(StringComparer.Ordinal);

        foreach (var alert in alerts)
        {
            foreach (var (key, maWindow) in AlertStateKeyPairsOf(alert))
            {
                currentKeys.Add(key);
                if (previousByKey.TryGetValue(key, out var previous) && previous.IsActive)
                {
                    // 條件持續成立：只更新最後判斷時間，不重複通知。
                    statesToPersist.Add(previous with
                    {
                        BaselineTradeDate = baselineTradeDate,
                        LastEvaluatedAt = evaluatedAt
                    });
                }
                else
                {
                    // 不成立（或從未出現、或已清除）→ 成立：新通知。
                    newlyTriggeredKeys.Add(key);
                    statesToPersist.Add(new IntradayAlertStateRecord(
                        evaluationDate, baselineTradeDate, workbookPath, alert.SheetName, alert.ExcelRow,
                        alert.StockCode, alert.AlertKind, maWindow,
                        IsActive: true,
                        FirstTriggeredAt: evaluatedAt,
                        LastEvaluatedAt: evaluatedAt,
                        LastNotifiedAt: evaluatedAt,
                        ClearedAt: null));
                }
            }
        }

        // 上一次成立、本次不成立（含 DDE 失效導致無法確認成立）→ 記錄清除；之後再次成立可再次通知。
        foreach (var previous in previousStates)
        {
            if (previous.IsActive && !currentKeys.Contains(StateKeyOf(previous)))
            {
                statesToPersist.Add(previous with
                {
                    IsActive = false,
                    LastEvaluatedAt = evaluatedAt,
                    ClearedAt = evaluatedAt
                });
            }
        }

        return (statesToPersist, newlyTriggeredKeys);
    }

    private static IEnumerable<string> AlertStateKeysOf(StrategyAlert alert)
        => AlertStateKeyPairsOf(alert).Select(x => x.Key);

    /// <summary>
    /// 把一筆 StrategyAlert 展開為去重狀態鍵。2026-07-19 起 MA5＋MA20 複合策略是不可拆開的單一條件，
    /// 每筆通知（含均線複合觸發、價格異常、缺技術資料）都只對應一個 MaWindow=0 的狀態鍵，
    /// 同一持股每次複合條件成立只產生一筆通知，不得分別對 MA5 與 MA20 各跳一次。
    /// </summary>
    private static IEnumerable<(string Key, int MaWindow)> AlertStateKeyPairsOf(StrategyAlert alert)
    {
        yield return (BuildStateKey(alert.SheetName, alert.ExcelRow, alert.StockCode, alert.AlertKind, 0), 0);
    }

    private static string StateKeyOf(IntradayAlertStateRecord state)
        => BuildStateKey(state.SheetName, state.ExcelRow, state.StockCode, state.AlertKind, state.MaWindow);

    private static string BuildStateKey(string sheetName, int excelRow, string stockCode, AlertKind alertKind, int maWindow)
        => $"{sheetName}|{excelRow}|{stockCode.ToUpperInvariant()}|{(int)alertKind}|{maWindow}";

    private async Task SaveRunAsync(
        IntradayRunSummary summary,
        DateTimeOffset? startedAt,
        string? skippedReason,
        string? errorMessage,
        CancellationToken cancellationToken)
    {
        try
        {
            var record = new IntradayEvaluationRunRecord(
                0,
                summary.EvaluationDate,
                summary.BaselineTradeDate,
                summary.ScheduledAt,
                startedAt,
                _clock.GetTaipeiNow(),
                summary.Status,
                summary.HoldingCount,
                summary.ActiveTriggerCount,
                summary.NewNotificationCount,
                summary.EntryAveragePriceInvalidCount,
                summary.CurrentPriceInvalidCount,
                summary.MissingMovingAverageCount,
                skippedReason,
                errorMessage);
            await _intradayStateRepository.SaveEvaluationRunAsync(record, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            // 執行摘要寫入失敗不得讓下一分鐘的盤中判斷停擺，只記錄 Log 供追查。
            _logger.Error("盤中執行摘要寫入 SQLite 失敗。", ex);
        }
    }

    private static IntradayRunSummary CreateSummary(
        DateOnly evaluationDate,
        DateOnly? baselineTradeDate,
        DateTimeOffset scheduledAt,
        DateTimeOffset evaluatedAt,
        IntradayRunStatus status,
        string message,
        bool isManualRun,
        int holdingCount,
        int activeTriggerCount,
        int newNotificationCount,
        int entryAveragePriceInvalidCount,
        int currentPriceInvalidCount,
        int missingMovingAverageCount,
        IReadOnlyList<StrategyAlert> alerts,
        IReadOnlyList<StrategyAlert> newlyTriggeredAlerts)
        => new(
            evaluationDate, baselineTradeDate, scheduledAt, evaluatedAt, isManualRun, status, message,
            holdingCount, activeTriggerCount, newNotificationCount,
            entryAveragePriceInvalidCount, currentPriceInvalidCount, missingMovingAverageCount,
            alerts, newlyTriggeredAlerts);

    private void ShowStatus(string message, int percentComplete)
        => _userInteraction?.ShowStatus(message, percentComplete);

    private void ShowProgressDetail(string message)
        => _userInteraction?.ShowProgressDetail(message);

    private void PublishFinalStatus(IntradayRunSummary summary)
    {
        ShowProgressDetail(string.Empty);
        var percent = summary.Status is IntradayRunStatus.Succeeded or IntradayRunStatus.PartialSuccess ? 100 : 0;
        ShowStatus(summary.Message, percent);
    }
}
