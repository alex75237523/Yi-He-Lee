using YiHeLee.Domain;

namespace YiHeLee.Application.Abstractions;

public interface IExcelWorkbookService
{
    Task<IReadOnlyList<CustomerHolding>> ReadHoldingsAsync(
        AppSettings settings,
        DateOnly targetDate,
        CancellationToken cancellationToken,
        Action<string>? reportProgress = null);

    /// <summary>
    /// 覆寫輸出頁籤：results 必須是「每一筆有效持股」的完整計算結果（見 <see cref="HoldingStrategyResult"/>），
    /// 不論是否觸發、DDE 現價是否有效、均線是否因逐檔歷史不足而部分缺項，都必須輸出一列，不得只傳入
    /// 需要中央通知的 alerts 子集合。
    /// </summary>
    Task WriteStrategyResultsAsync(
        AppSettings settings,
        DateOnly targetDate,
        IReadOnlyList<HoldingStrategyResult> results,
        CancellationToken cancellationToken);
}
