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
    /// 覆寫「每日五日均價策略」頁籤；此頁籤只保存代碼、名稱、收盤價、5日均價、20日均價、60日均價、120日均價。
    /// 客戶、Excel 現價、DDE 狀態、觸發條件與診斷資訊只屬於下游客戶比對，不得寫入此頁籤。
    /// </summary>
    Task WriteStrategyResultsAsync(
        AppSettings settings,
        DateOnly targetDate,
        IReadOnlyList<DailyMovingAverageSnapshot> rows,
        CancellationToken cancellationToken);
}
