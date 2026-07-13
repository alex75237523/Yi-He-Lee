using YiHeLee.Domain;

namespace YiHeLee.Application.Abstractions;

/// <summary>
/// 盤中上一交易日基準資料準備服務。負責在基準缺漏時補齊官方收盤價、重算均價，
/// 並以既有 SQLite 資料推導 Ready 狀態；快速路徑不得呼叫官方 Provider。
/// </summary>
public interface IBaselinePreparationService
{
    Task<BaselinePreparationState> GetStateAsync(
        DateOnly evaluationDate,
        DateOnly? baselineTradeDate,
        CancellationToken cancellationToken);

    Task<BaselinePreparationResult> EnsureBaselineDataAsync(
        DateOnly evaluationDate,
        IntradayBaselineResolution initialResolution,
        OfficialMarketDataSettings settings,
        CancellationToken cancellationToken);
}
