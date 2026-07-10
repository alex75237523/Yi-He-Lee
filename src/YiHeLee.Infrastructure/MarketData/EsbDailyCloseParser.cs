using System.Text.Json;
using YiHeLee.Application.Exceptions;
using YiHeLee.Domain;

namespace YiHeLee.Infrastructure.MarketData;

public sealed record EsbDailyCloseParseResult(
    DateOnly? SourceDataDate,
    bool IsExplicitNoData,
    IReadOnlyList<OfficialPriceQuote> Quotes);

/// <summary>
/// 將 TPEx 官方 OpenAPI「興櫃股票當日行情表」(tpex_esb_latest_statistics) 回應轉成結構化資料。
/// 本端點沒有日期參數，只回報呼叫當下的即時快照；每一列都帶有自己的 Date 欄位（民國年緊湊格式），
/// 一律以第一筆有效資料列的日期為準，是否等於 targetDate 由 Service 層嚴格比對，
/// 不得只憑 HTTP 200 或有資料列視為當日成功。
/// </summary>
public static class EsbDailyCloseParser
{
    public static EsbDailyCloseParseResult Parse(string rawJson)
    {
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(rawJson);
        }
        catch (JsonException ex)
        {
            throw new RetryableJobException("TPEx 興櫃官方回應不是有效 JSON，可能為錯誤頁或驗證頁。", ex);
        }

        using (document)
        {
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Array)
            {
                throw new RetryableJobException("TPEx 興櫃官方回應不是預期的陣列格式，網站結構可能已變更。");
            }

            DateOnly? sourceDate = null;
            var quotes = new List<OfficialPriceQuote>();

            foreach (var row in root.EnumerateArray())
            {
                if (row.ValueKind != JsonValueKind.Object)
                {
                    continue;
                }

                var rawDate = row.TryGetProperty("Date", out var dateElement) ? dateElement.GetString() : null;
                if (sourceDate is null && RocDateConverter.TryParseRocCompact(rawDate, out var rowDate))
                {
                    sourceDate = rowDate;
                }

                var code = MarketDataParsingHelpers.Normalize(
                    row.TryGetProperty("SecuritiesCompanyCode", out var codeElement) ? codeElement.GetString() : null);
                if (string.IsNullOrWhiteSpace(code))
                {
                    continue;
                }

                var name = MarketDataParsingHelpers.Normalize(
                    row.TryGetProperty("CompanyName", out var nameElement) ? nameElement.GetString() : null);

                // 興櫃無漲跌停、逐筆議價成交，當日成交價以 LatestPrice（最後成交價）為準；
                // 全日無成交或剛登錄興櫃尚無成交紀錄時 LatestPrice 為空字串，該列略過但不影響其餘資料。
                var rawPrice = row.TryGetProperty("LatestPrice", out var priceElement) ? priceElement.GetString() : null;
                if (!MarketDataParsingHelpers.TryParsePrice(rawPrice, out var closePrice))
                {
                    continue;
                }

                quotes.Add(new OfficialPriceQuote(code, name, closePrice));
            }

            var explicitNoData = sourceDate is null && quotes.Count == 0;
            return new EsbDailyCloseParseResult(sourceDate, explicitNoData, quotes);
        }
    }
}
