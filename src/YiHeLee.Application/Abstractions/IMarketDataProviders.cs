using YiHeLee.Domain;

namespace YiHeLee.Application.Abstractions;

/// <summary>
/// 臺灣證券交易所（TWSE）官方每日收盤價來源。
/// 只負責呼叫官方端點、取得原始回應、轉成 <see cref="OfficialPriceFetchResult"/>，
/// 並回報來源自己聲明的資料日期；是否等於 targetDate 由 Service 層驗證，Provider 不得自行判斷成功與否。
/// </summary>
public interface ITwseMarketDataProvider
{
    /// <summary>來源顯示名稱，供批次紀錄與通知使用。</summary>
    string SourceProviderName { get; }

    Task<OfficialPriceFetchResult> FetchDailyCloseAsync(
        DateOnly requestedDate,
        OfficialMarketDataSettings settings,
        CancellationToken cancellationToken);
}

/// <summary>
/// 證券櫃檯買賣中心（TPEx）官方每日收盤價來源。
/// 職責與 <see cref="ITwseMarketDataProvider"/> 相同，僅來源與日期格式（民國年）不同。
/// </summary>
public interface ITpexMarketDataProvider
{
    string SourceProviderName { get; }

    Task<OfficialPriceFetchResult> FetchDailyCloseAsync(
        DateOnly requestedDate,
        OfficialMarketDataSettings settings,
        CancellationToken cancellationToken);
}
