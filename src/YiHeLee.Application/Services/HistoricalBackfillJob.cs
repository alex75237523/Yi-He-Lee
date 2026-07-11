using YiHeLee.Application.Abstractions;
using YiHeLee.Domain;

namespace YiHeLee.Application.Services;

/// <summary>
/// 歷史資料回補排程單元：與每日正式排程分開，只負責觸發 <see cref="IMarketPriceService"/> 往前補建，
/// 直到累積足夠的有效交易日供 MA120 計算為止。歷史資料一律保存原始 TradeDate，不得改寫為執行當日。
/// </summary>
public sealed class HistoricalBackfillJob
{
    private readonly IMarketPriceService _marketPriceService;

    public HistoricalBackfillJob(IMarketPriceService marketPriceService)
    {
        _marketPriceService = marketPriceService;
    }

    public Task<IReadOnlyList<OfficialPriceBatchSummary>> RunAsync(
        DateOnly targetDate,
        OfficialMarketDataSettings settings,
        CancellationToken cancellationToken,
        Action<string>? reportProgress = null,
        IReadOnlyCollection<string>? emergingStockCodes = null,
        IReadOnlyCollection<string>? listedStockCodes = null,
        IReadOnlyCollection<string>? otcStockCodes = null)
        => _marketPriceService.BackfillHistoryAsync(
            targetDate, settings, cancellationToken, reportProgress, emergingStockCodes, listedStockCodes, otcStockCodes);
}
