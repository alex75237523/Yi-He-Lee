using System.Collections.Concurrent;
using YiHeLee.Application.Abstractions;
using YiHeLee.Domain;

namespace YiHeLee.Application.Services;

/// <summary>
/// 盤中基準資料準備服務。
/// Ready 狀態由 StockDailyPrice 與 StockMovingAverage 既有資料推導；本服務只用記憶體鎖避免同時回補，
/// 不用記憶體 bool 當作完成狀態。
/// </summary>
public sealed class BaselinePreparationService : IBaselinePreparationService
{
    private static readonly ConcurrentDictionary<string, SemaphoreSlim> PreparationLocks = new(StringComparer.Ordinal);

    private readonly IMarketDataRepository _marketDataRepository;
    private readonly IMarketPriceService _marketPriceService;
    private readonly IMovingAverageService _movingAverageService;
    private readonly ITradingDateResolver _tradingDateResolver;
    private readonly IClock _clock;
    private readonly IUserInteraction _userInteraction;
    private readonly IAppLogger _logger;

    public BaselinePreparationService(
        IMarketDataRepository marketDataRepository,
        IMarketPriceService marketPriceService,
        IMovingAverageService movingAverageService,
        ITradingDateResolver tradingDateResolver,
        IClock clock,
        IUserInteraction userInteraction,
        IAppLogger logger)
    {
        _marketDataRepository = marketDataRepository;
        _marketPriceService = marketPriceService;
        _movingAverageService = movingAverageService;
        _tradingDateResolver = tradingDateResolver;
        _clock = clock;
        _userInteraction = userInteraction;
        _logger = logger;
    }

    public async Task<BaselinePreparationState> GetStateAsync(
        DateOnly evaluationDate,
        DateOnly? baselineTradeDate,
        CancellationToken cancellationToken)
        => await DeriveStateAsync(evaluationDate, baselineTradeDate, cancellationToken).ConfigureAwait(false);

    public async Task<BaselinePreparationResult> EnsureBaselineDataAsync(
        DateOnly evaluationDate,
        IntradayBaselineResolution initialResolution,
        OfficialMarketDataSettings settings,
        CancellationToken cancellationToken)
    {
        var key = evaluationDate.ToString("yyyy-MM-dd");
        var semaphore = PreparationLocks.GetOrAdd(key, _ => new SemaphoreSlim(1, 1));
        var candidateDate = ResolveCandidateBaselineDate(initialResolution);
        var initialState = await DeriveStateAsync(evaluationDate, candidateDate, cancellationToken).ConfigureAwait(false);
        if (initialState.Status == BaselinePreparationStatus.Ready)
        {
            return new BaselinePreparationResult(
                initialState,
                PreparedThisRun: false,
                IsAnotherPreparationRunning: false,
                $"基準資料已就緒，沿用 {candidateDate:yyyy-MM-dd} 已完成均價。");
        }

        if (!await semaphore.WaitAsync(0, cancellationToken).ConfigureAwait(false))
        {
            var runningState = initialState with
            {
                Status = BaselinePreparationStatus.Backfilling,
                LastError = "另一個盤中流程正在準備上一交易日資料。"
            };
            return new BaselinePreparationResult(
                runningState,
                PreparedThisRun: false,
                IsAnotherPreparationRunning: true,
                "盤中監控：正在準備上一交易日資料，本次 Tick 略過，不排隊。");
        }

        try
        {
            _userInteraction.ShowStatus("盤中監控：正在準備基準資料", 20);
            PublishPreparationDetail(evaluationDate, candidateDate, "補齊官方收盤價／計算均價");

            var preparedThisRun = false;
            var resolution = initialResolution;
            var state = initialState;

            if (!state.OfficialPriceReady)
            {
                _logger.Info($"盤中基準資料缺官方收盤價，開始自動回補。EvaluationDate={evaluationDate:yyyy-MM-dd}，ExpectedBaseline={candidateDate?.ToString("yyyy-MM-dd") ?? "未知"}。");
                PublishPreparationDetail(evaluationDate, candidateDate, "補齊官方收盤價");
                var backfillSummaries = await _marketPriceService.BackfillHistoryAsync(
                    evaluationDate,
                    settings,
                    cancellationToken,
                    detail => PublishPreparationDetail(evaluationDate, candidateDate, detail)).ConfigureAwait(false);
                preparedThisRun = backfillSummaries.Count > 0;

                resolution = await _tradingDateResolver.ResolveBaselineAsync(evaluationDate, cancellationToken).ConfigureAwait(false);
                candidateDate = ResolveCandidateBaselineDate(resolution);
                state = await DeriveStateAsync(evaluationDate, candidateDate, cancellationToken).ConfigureAwait(false);
            }

            if (candidateDate is null)
            {
                var failed = state with
                {
                    Status = BaselinePreparationStatus.Failed,
                    LastError = resolution.NotReadyReason ?? "回補後仍無法解析上一交易日基準。"
                };
                return new BaselinePreparationResult(failed, preparedThisRun, false, failed.LastError!);
            }

            if (!state.OfficialPriceReady)
            {
                var failed = state with
                {
                    Status = BaselinePreparationStatus.Failed,
                    LastError = $"回補後仍找不到 {candidateDate:yyyy-MM-dd} 的官方收盤價。"
                };
                return new BaselinePreparationResult(failed, preparedThisRun, false, failed.LastError!);
            }

            if (!state.MovingAverageReady)
            {
                _logger.Info($"盤中基準資料缺均價快照，開始重新計算。BaselineTradeDate={candidateDate:yyyy-MM-dd}。");
                PublishPreparationDetail(evaluationDate, candidateDate, "計算均價");
                var stockCodes = await _marketDataRepository.GetStockCodesWithDailyPriceAsync(candidateDate.Value, cancellationToken).ConfigureAwait(false);
                if (stockCodes.Count == 0)
                {
                    var failed = state with
                    {
                        Status = BaselinePreparationStatus.Failed,
                        LastError = $"{candidateDate:yyyy-MM-dd} 沒有可用官方收盤價股票，無法計算均價。"
                    };
                    return new BaselinePreparationResult(failed, preparedThisRun, false, failed.LastError!);
                }

                var movingAverages = await _movingAverageService.CalculateManyAsync(stockCodes, candidateDate.Value, cancellationToken).ConfigureAwait(false);
                await _marketDataRepository.SaveMovingAverageResultsAsync(candidateDate.Value, movingAverages, cancellationToken).ConfigureAwait(false);
                preparedThisRun = true;
                state = await DeriveStateAsync(evaluationDate, candidateDate, cancellationToken).ConfigureAwait(false);
            }

            if (state.Status == BaselinePreparationStatus.Ready)
            {
                var ready = state with { CompletedAt = _clock.GetTaipeiNow(), LastError = null };
                _logger.Info($"盤中基準資料準備完成。EvaluationDate={evaluationDate:yyyy-MM-dd}，BaselineTradeDate={candidateDate:yyyy-MM-dd}。");
                return new BaselinePreparationResult(
                    ready,
                    preparedThisRun,
                    false,
                    preparedThisRun
                        ? $"基準資料已準備完成：{candidateDate:yyyy-MM-dd} 官方收盤價與 MA5／MA20／MA60／MA120 已保存。"
                        : $"基準資料已就緒，沿用 {candidateDate:yyyy-MM-dd} 已完成均價。");
            }

            var partial = state with
            {
                Status = BaselinePreparationStatus.Partial,
                LastError = $"{candidateDate:yyyy-MM-dd} 基準資料仍不完整：官方收盤價 {state.OfficialPriceStockCount} 檔、完整均價 {state.MovingAverageStockCount} 檔。"
            };
            return new BaselinePreparationResult(partial, preparedThisRun, false, partial.LastError!);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.Error($"盤中基準資料準備失敗。EvaluationDate={evaluationDate:yyyy-MM-dd}。", ex);
            var failed = new BaselinePreparationState(
                evaluationDate,
                candidateDate,
                CalendarResolved: candidateDate is not null,
                OfficialPriceReady: false,
                MovingAverageReady: false,
                BaselinePreparationStatus.Failed,
                CompletedAt: null,
                LastError: ex.Message,
                OfficialPriceStockCount: 0,
                MovingAverageStockCount: 0);
            return new BaselinePreparationResult(failed, PreparedThisRun: false, IsAnotherPreparationRunning: false, ex.Message);
        }
        finally
        {
            _userInteraction.ShowProgressDetail(string.Empty);
            semaphore.Release();
        }
    }

    private async Task<BaselinePreparationState> DeriveStateAsync(
        DateOnly evaluationDate,
        DateOnly? baselineTradeDate,
        CancellationToken cancellationToken)
    {
        if (baselineTradeDate is null)
        {
            return new BaselinePreparationState(
                evaluationDate, null, CalendarResolved: false,
                OfficialPriceReady: false, MovingAverageReady: false,
                BaselinePreparationStatus.Unknown, null,
                "尚未能解析上一交易日基準日期。", 0, 0);
        }

        var officialStockCodes = (await _marketDataRepository
                .GetStockCodesWithDailyPriceAsync(baselineTradeDate.Value, cancellationToken).ConfigureAwait(false))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var movingAverages = await _marketDataRepository
            .GetMovingAverageResultsAsync(baselineTradeDate.Value, cancellationToken).ConfigureAwait(false);
        var movingAverageByCode = movingAverages
            .GroupBy(x => x.StockCode, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(x => x.Key, x => x.First(), StringComparer.OrdinalIgnoreCase);

        var officialReady = officialStockCodes.Length > 0;
        var completeMovingAverageCount = officialStockCodes.Count(code =>
            movingAverageByCode.TryGetValue(code, out var row)
            && row.CalculationStatus == CalculationStatus.Ok
            && row.ClosePrice is not null
            && row.MovingAverage5 is not null
            && row.MovingAverage20 is not null
            && row.MovingAverage60 is not null
            && row.MovingAverage120 is not null);
        var movingAverageReady = officialReady && completeMovingAverageCount == officialStockCodes.Length;

        var status = movingAverageReady
            ? BaselinePreparationStatus.Ready
            : !officialReady
                ? BaselinePreparationStatus.Unknown
                : movingAverages.Count > 0
                    ? BaselinePreparationStatus.Partial
                    : BaselinePreparationStatus.CalculatingMovingAverage;

        var lastError = status switch
        {
            BaselinePreparationStatus.Ready => null,
            BaselinePreparationStatus.Unknown => $"{baselineTradeDate:yyyy-MM-dd} 尚無官方收盤價。",
            BaselinePreparationStatus.CalculatingMovingAverage => $"{baselineTradeDate:yyyy-MM-dd} 尚無均價快照。",
            _ => $"{baselineTradeDate:yyyy-MM-dd} 均價快照不完整，完整均價 {completeMovingAverageCount}/{officialStockCodes.Length} 檔。"
        };

        return new BaselinePreparationState(
            evaluationDate,
            baselineTradeDate,
            CalendarResolved: true,
            OfficialPriceReady: officialReady,
            MovingAverageReady: movingAverageReady,
            status,
            status == BaselinePreparationStatus.Ready ? _clock.GetTaipeiNow() : null,
            lastError,
            officialStockCodes.Length,
            completeMovingAverageCount);
    }

    private void PublishPreparationDetail(DateOnly evaluationDate, DateOnly? baselineTradeDate, string step)
    {
        _userInteraction.ShowProgressDetail(
            $"判斷日期：{evaluationDate:yyyy-MM-dd}；" +
            $"預期均價基準日期：{(baselineTradeDate is DateOnly d ? d.ToString("yyyy-MM-dd") : "解析中")}；" +
            $"目前步驟：{step}");
    }

    private static DateOnly? ResolveCandidateBaselineDate(IntradayBaselineResolution resolution)
        => resolution.BaselineTradeDate
           ?? resolution.ExpectedBaselineTradeDate
           ?? (resolution.LatestMovingAverageTradeDate == resolution.LatestPriceTradeDate ? resolution.LatestPriceTradeDate : null);
}
