using YiHeLee.Application.Abstractions;
using YiHeLee.Domain;

namespace YiHeLee.Application.Services;

/// <summary>
/// 每日正式官方價格排程單元：只負責觸發 <see cref="IMarketPriceService"/> 抓取 targetDate 當日資料，
/// 不得直接發出 HTTP 請求、解析 HTML／JSON 或組 SQL；絕對禁止回抓前一交易日。
/// </summary>
public sealed class DailyMarketDataJob
{
    private readonly IMarketPriceService _marketPriceService;

    public DailyMarketDataJob(IMarketPriceService marketPriceService)
    {
        _marketPriceService = marketPriceService;
    }

    public Task<IReadOnlyList<OfficialPriceBatchSummary>> RunAsync(
        DateOnly targetDate,
        OfficialMarketDataSettings settings,
        CancellationToken cancellationToken)
        => _marketPriceService.FetchAndSaveDailyPricesAsync(targetDate, settings, cancellationToken);
}
