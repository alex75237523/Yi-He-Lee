using YiHeLee.Application.Abstractions;
using YiHeLee.Domain;

namespace YiHeLee.Application.Services;

/// <summary>
/// 以資料庫已保存的官方資料解析「真正的上一交易日」（2026-07-13 盤中／收盤流程拆分新增）。
/// 禁止使用 <c>today.AddDays(-1)</c>：星期一的上一交易日是上星期五，國定假日、颱風休市後
/// 第一個交易日的上一交易日可能相隔數天。判定依據：
/// 1. StockDailyPrice 已保存官方收盤價的最新交易日（真正上一交易日）。
/// 2. 每日排程曾嘗試但未成功的收盤更新（OfficialPriceBatch 非 Holiday 批次）晚於該日期時，
///    代表上一交易日收盤更新失敗，基準不得使用更舊資料冒充。
/// 3. StockMovingAverage 均價快照最新日期必須等於上一交易日，否則視為快照不完整。
/// 任一條件不符即回報「基準均價資料尚未就緒」，禁止自動退回更舊的均價資料。
/// </summary>
public sealed class TradingDateResolver : ITradingDateResolver
{
    private readonly IMarketDataRepository _marketDataRepository;

    public TradingDateResolver(IMarketDataRepository marketDataRepository)
    {
        _marketDataRepository = marketDataRepository;
    }

    public async Task<IntradayBaselineResolution> ResolveBaselineAsync(DateOnly evaluationDate, CancellationToken cancellationToken)
    {
        var latestPriceDate = await _marketDataRepository
            .GetLatestPriceTradeDateBeforeAsync(evaluationDate, cancellationToken).ConfigureAwait(false);
        var latestMovingAverageDate = await _marketDataRepository
            .GetLatestMovingAverageTradeDateBeforeAsync(evaluationDate, cancellationToken).ConfigureAwait(false);

        if (latestPriceDate is null)
        {
            return new IntradayBaselineResolution(
                evaluationDate, null, false,
                $"資料庫尚無 {evaluationDate:yyyy-MM-dd} 之前的官方收盤價資料，無法解析上一交易日。請先執行收盤更新或歷史回補。",
                null, latestMovingAverageDate);
        }

        var latestAttemptDate = await _marketDataRepository
            .GetLatestDailyCloseAttemptDateBeforeAsync(evaluationDate, cancellationToken).ConfigureAwait(false);
        if (latestAttemptDate is DateOnly attempt && attempt > latestPriceDate.Value)
        {
            // 更晚的日期曾嘗試收盤更新但沒有留下任何官方收盤價，代表上一交易日收盤更新未成功；
            // 禁止使用 latestPriceDate（兩個以上交易日前）的舊均價冒充上一交易日基準。
            return new IntradayBaselineResolution(
                evaluationDate, null, false,
                $"上一交易日 {attempt:yyyy-MM-dd} 的收盤更新尚未成功（官方收盤價未保存），" +
                $"禁止退回 {latestPriceDate.Value:yyyy-MM-dd} 的舊均價冒充上一交易日。請先執行收盤更新或歷史回補。",
                latestPriceDate, latestMovingAverageDate);
        }

        if (latestMovingAverageDate is null || latestMovingAverageDate.Value != latestPriceDate.Value)
        {
            return new IntradayBaselineResolution(
                evaluationDate, null, false,
                $"上一交易日 {latestPriceDate.Value:yyyy-MM-dd} 的均價快照不存在或不完整" +
                $"（均價快照最新日期：{(latestMovingAverageDate is DateOnly ma ? ma.ToString("yyyy-MM-dd") : "無")}），" +
                "禁止退回更舊的均價快照。請先執行收盤更新或歷史回補。",
                latestPriceDate, latestMovingAverageDate);
        }

        return new IntradayBaselineResolution(
            evaluationDate, latestPriceDate.Value, true, null, latestPriceDate, latestMovingAverageDate);
    }

    public async Task<bool> IsKnownNonTradingDayAsync(DateOnly date, CancellationToken cancellationToken)
    {
        if (date.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday)
        {
            return true;
        }

        return await _marketDataRepository.HasHolidayBatchAsync(date, cancellationToken).ConfigureAwait(false);
    }
}
