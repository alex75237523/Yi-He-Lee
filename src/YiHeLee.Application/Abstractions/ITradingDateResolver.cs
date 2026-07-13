using YiHeLee.Domain;

namespace YiHeLee.Application.Abstractions;

/// <summary>
/// 上一交易日與非交易日判定（2026-07-13 盤中／收盤流程拆分新增）。
/// 禁止使用 <c>today.AddDays(-1)</c>：星期一、國定假日、颱風休市等情況，前一天不一定是交易日。
/// 判定依據優先使用 OfficialPriceBatch 正式成功的官方收盤批次、明確記錄的 Holiday／NoTradingData，
/// 以及已完整保存的 StockMovingAverage 快照。
/// </summary>
public interface ITradingDateResolver
{
    /// <summary>
    /// 解析 <paramref name="evaluationDate"/>（盤中判斷日）應使用的上一交易日均價基準。
    /// 真正上一交易日的均價快照不存在、不完整或收盤更新失敗時，回傳「基準均價資料尚未就緒」，
    /// 禁止自動退回更舊的均價資料、禁止以兩個交易日前的資料冒充上一交易日。
    /// </summary>
    Task<IntradayBaselineResolution> ResolveBaselineAsync(DateOnly evaluationDate, CancellationToken cancellationToken);

    /// <summary>
    /// 是否為「已知的非交易日」：週六／週日，或官方批次已明確記錄為休市的日期。
    /// 台灣國定假日在收盤批次記錄休市前無法離線得知，因此本方法只能排除已知情況；
    /// 未知情況仍視為候選交易日，由盤中判斷自行以資料狀態呈現。
    /// </summary>
    Task<bool> IsKnownNonTradingDayAsync(DateOnly date, CancellationToken cancellationToken);
}
