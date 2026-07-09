using YiHeLee.Domain;

namespace YiHeLee.Application.Abstractions;

/// <summary>
/// 依官方收盤價計算 MA5／MA20／MA60／MA120。一律以「有效交易日」（非日曆日）計算，
/// 中間過程不四捨五入；資料不足時對應欄位為 null，不得以較少天數硬算。
/// </summary>
public interface IMovingAverageService
{
    Task<MovingAverageResult> CalculateAsync(
        string stockCode,
        DateOnly tradeDate,
        CancellationToken cancellationToken);

    Task<IReadOnlyList<MovingAverageResult>> CalculateManyAsync(
        IReadOnlyCollection<string> stockCodes,
        DateOnly tradeDate,
        CancellationToken cancellationToken);
}
