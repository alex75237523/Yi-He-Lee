using YiHeLee.Domain;

namespace YiHeLee.Application.Abstractions;

/// <summary>
/// 官方自算均線與鉅亨網多頭／空頭排列清單的交叉驗證服務。僅作驗證與異常紀錄，
/// 鉅亨網資料不得覆蓋或取代官方歷史收盤價與自算均線結果。
/// </summary>
public interface IStockPriceValidationService
{
    /// <summary>
    /// 驗證 <paramref name="tradeDate"/> 當日，<paramref name="stockMarketTypes"/> 內每一檔股票的自算均線
    /// 與 <paramref name="cnyesBatches"/>（呼叫端已取得的鉅亨網多頭／空頭排列清單）是否相符。
    /// </summary>
    Task<IReadOnlyList<CnyesValidationRecord>> ValidateAsync(
        DateOnly tradeDate,
        IReadOnlyDictionary<string, MarketType> stockMarketTypes,
        IReadOnlyList<CrawlBatch> cnyesBatches,
        CancellationToken cancellationToken);
}
