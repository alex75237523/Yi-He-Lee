using YiHeLee.Application.Abstractions;
using YiHeLee.Domain;

namespace YiHeLee.Application.Services;

/// <summary>
/// 依官方收盤價計算 MA5／MA20／MA60／MA120。
/// 只依「有效交易日」（資料庫實際保存的交易日）計算，不使用日曆日；中間計算不四捨五入；
/// 任一均線所需天數不足時，該均線欄位為 null，不得以較少天數硬算。
/// </summary>
public sealed class MovingAverageService : IMovingAverageService
{
    private static readonly int[] Windows = [5, 20, 60, 120];

    private readonly IMarketDataRepository _repository;

    public MovingAverageService(IMarketDataRepository repository)
    {
        _repository = repository;
    }

    public async Task<MovingAverageResult> CalculateAsync(string stockCode, DateOnly tradeDate, CancellationToken cancellationToken)
    {
        var maxWindow = Windows[^1];
        var history = await _repository.GetRecentClosePricesAsync(stockCode, tradeDate, maxWindow, cancellationToken).ConfigureAwait(false);
        return Calculate(stockCode, tradeDate, history);
    }

    public async Task<IReadOnlyList<MovingAverageResult>> CalculateManyAsync(
        IReadOnlyCollection<string> stockCodes,
        DateOnly tradeDate,
        CancellationToken cancellationToken)
    {
        var results = new List<MovingAverageResult>(stockCodes.Count);
        foreach (var stockCode in stockCodes.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            cancellationToken.ThrowIfCancellationRequested();
            results.Add(await CalculateAsync(stockCode, tradeDate, cancellationToken).ConfigureAwait(false));
        }

        return results;
    }

    /// <summary>
    /// history 依交易日期由新到舊排序；最新一筆若等於 tradeDate 即當日收盤價，
    /// 否則代表當日尚無官方收盤價資料，整筆視為無法判斷（由呼叫端另行處理，此處仍會回傳交易日數為 0 的結果）。
    /// </summary>
    internal static MovingAverageResult Calculate(string stockCode, DateOnly tradeDate, IReadOnlyList<(DateOnly TradeDate, decimal ClosePrice)> rawHistory)
    {
        // 防禦性去重：同一天同一股票只能有一筆正式收盤價；即使上游意外提供重複交易日，
        // 也不得讓同一天重複計入平均（正式資料庫已有 UNIQUE 約束，此處為第二層保護）。
        var history = rawHistory
            .GroupBy(x => x.TradeDate)
            .Select(g => g.First())
            .OrderByDescending(x => x.TradeDate)
            .ToArray();

        if (history.Length == 0 || history[0].TradeDate != tradeDate)
        {
            // 最新一筆官方收盤價日期不等於策略日期，代表當日收盤價尚未取得，
            // 與「歷史交易日數不足」是不同原因，必須明確區分，不得混為一談或用昨日資料頂替。
            DateOnly? latest = history.Length > 0 ? history[0].TradeDate : null;
            var missingReason = history.Length == 0
                ? "尚無任何官方收盤價資料。"
                : $"官方收盤價最新日期為 {latest:yyyy-MM-dd}，尚未等於指定策略日期 {tradeDate:yyyy-MM-dd}，依規定不得以昨日資料替代。";
            return new MovingAverageResult(stockCode, tradeDate, null, null, null, null, null, 0, CalculationStatus.TodayCloseMissing, latest, missingReason);
        }

        var closePrice = history[0].ClosePrice;
        var availableCount = history.Length;

        decimal? ma5 = ComputeAverage(history, 5);
        decimal? ma20 = ComputeAverage(history, 20);
        decimal? ma60 = ComputeAverage(history, 60);
        decimal? ma120 = ComputeAverage(history, 120);

        var status = ma5 is null || ma20 is null || ma60 is null || ma120 is null
            ? CalculationStatus.InsufficientHistory
            : CalculationStatus.Ok;

        var missingReasonText = status == CalculationStatus.InsufficientHistory
            ? $"僅累積 {availableCount} 個有效交易日，MA120 尚缺 {Math.Max(0, 120 - availableCount)} 個有效交易日（逐檔檢查，非市場整體交易日數）。"
            : null;

        return new MovingAverageResult(stockCode, tradeDate, closePrice, ma5, ma20, ma60, ma120, availableCount, status, history[0].TradeDate, missingReasonText);
    }

    private static decimal? ComputeAverage(IReadOnlyList<(DateOnly TradeDate, decimal ClosePrice)> history, int window)
    {
        if (history.Count < window)
        {
            return null;
        }

        decimal sum = 0m;
        for (var i = 0; i < window; i++)
        {
            sum += history[i].ClosePrice;
        }

        // 中間計算不先四捨五入；除法保留 decimal 完整精度，顯示層再依需求格式化為小數點後2位。
        return sum / window;
    }
}
