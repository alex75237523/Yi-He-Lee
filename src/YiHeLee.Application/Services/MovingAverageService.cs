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
    internal static MovingAverageResult Calculate(string stockCode, DateOnly tradeDate, IReadOnlyList<(DateOnly TradeDate, decimal ClosePrice)> history)
    {
        if (history.Count == 0 || history[0].TradeDate != tradeDate)
        {
            return new MovingAverageResult(stockCode, tradeDate, null, null, null, null, null, 0, CalculationStatus.InsufficientHistory);
        }

        var closePrice = history[0].ClosePrice;
        var availableCount = history.Count;

        decimal? ma5 = ComputeAverage(history, 5);
        decimal? ma20 = ComputeAverage(history, 20);
        decimal? ma60 = ComputeAverage(history, 60);
        decimal? ma120 = ComputeAverage(history, 120);

        var status = ma5 is null || ma20 is null || ma60 is null || ma120 is null
            ? CalculationStatus.InsufficientHistory
            : CalculationStatus.Ok;

        return new MovingAverageResult(stockCode, tradeDate, closePrice, ma5, ma20, ma60, ma120, availableCount, status);
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
