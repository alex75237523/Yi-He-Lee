using System.Text.Json;
using YiHeLee.Application.Exceptions;
using YiHeLee.Domain;

namespace YiHeLee.Infrastructure.MarketData;

public sealed record TpexDailyCloseParseResult(
    DateOnly? SourceDataDate,
    bool IsExplicitNoData,
    IReadOnlyList<OfficialPriceQuote> Quotes);

/// <summary>
/// 將 TPEx 官方「上櫃股票行情」(stk_quote_result.php) 回應轉成結構化資料。
/// 重要：本端點對不存在或非交易日的查詢日期，會「靜默」改回傳最近一個有交易的資料日期，
/// 不會回報錯誤或明確的休市訊息；因此本 Parser 一律誠實回報來源實際回傳的日期，
/// 是否等於 targetDate 必須由 Service 層嚴格比對，禁止只憑 HTTP 200 或有資料列視為當日成功。
/// </summary>
public static class TpexDailyCloseParser
{
    public static TpexDailyCloseParseResult Parse(string rawJson)
    {
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(rawJson);
        }
        catch (JsonException ex)
        {
            throw new RetryableJobException("TPEx 官方回應不是有效 JSON，可能為錯誤頁或驗證頁。", ex);
        }

        using (document)
        {
            var root = document.RootElement;

            DateOnly? sourceDate = null;
            if (root.TryGetProperty("date", out var dateElement) && dateElement.ValueKind == JsonValueKind.String)
            {
                if (RocDateConverter.TryParseWesternCompact(dateElement.GetString(), out var parsedWestern))
                {
                    sourceDate = parsedWestern;
                }
            }

            if (!root.TryGetProperty("tables", out var tablesElement) || tablesElement.ValueKind != JsonValueKind.Array
                || tablesElement.GetArrayLength() == 0)
            {
                // 沒有 date 也沒有 tables，視為明確查無資料（例如日期早於 TPEx 資料涵蓋範圍）。
                if (sourceDate is null)
                {
                    return new TpexDailyCloseParseResult(null, true, []);
                }

                throw new RetryableJobException("TPEx 回應缺少 tables 陣列，網站結構可能已變更。");
            }

            var table = tablesElement[0];

            // 表格內也帶有自己的民國年日期欄位，若與頂層西元日期換算不一致，以表格內日期為準並記錄。
            if (table.TryGetProperty("date", out var tableDateElement) && tableDateElement.ValueKind == JsonValueKind.String
                && RocDateConverter.TryParseRocSlash(tableDateElement.GetString(), out var tableDate))
            {
                sourceDate = tableDate;
            }

            if (!table.TryGetProperty("fields", out var fieldsElement) || fieldsElement.ValueKind != JsonValueKind.Array)
            {
                throw new RetryableJobException("TPEx 回應表格缺少 fields 欄位定義，網站結構可能已變更。");
            }

            var totalCountIsZero = table.TryGetProperty("totalCount", out var totalCountElement)
                && totalCountElement.TryGetInt32(out var totalCount)
                && totalCount == 0;
            var dataIsEmpty = !table.TryGetProperty("data", out var rawDataElement)
                || rawDataElement.ValueKind != JsonValueKind.Array
                || rawDataElement.GetArrayLength() == 0;
            if (sourceDate is not null && totalCountIsZero && dataIsEmpty)
            {
                return new TpexDailyCloseParseResult(sourceDate, true, []);
            }

            var fieldNames = fieldsElement.EnumerateArray()
                .Select(x => MarketDataParsingHelpers.Normalize(x.GetString()))
                .ToArray();
            var codeIndex = Array.IndexOf(fieldNames, "代號");
            var nameIndex = Array.IndexOf(fieldNames, "名稱");
            var closeIndex = Array.IndexOf(fieldNames, "收盤");
            if (codeIndex < 0 || nameIndex < 0 || closeIndex < 0)
            {
                throw new RetryableJobException("TPEx 每日收盤行情表格欄位不完整（缺少代號／名稱／收盤），網站結構可能已變更。");
            }

            var quotes = new List<OfficialPriceQuote>();
            if (table.TryGetProperty("data", out var dataElement) && dataElement.ValueKind == JsonValueKind.Array)
            {
                foreach (var row in dataElement.EnumerateArray())
                {
                    if (row.ValueKind != JsonValueKind.Array)
                    {
                        continue;
                    }

                    var cells = row.EnumerateArray().Select(x => x.GetString() ?? string.Empty).ToArray();
                    if (cells.Length <= Math.Max(codeIndex, Math.Max(nameIndex, closeIndex)))
                    {
                        continue;
                    }

                    var code = MarketDataParsingHelpers.Normalize(cells[codeIndex]);
                    var name = MarketDataParsingHelpers.Normalize(cells[nameIndex]);
                    if (string.IsNullOrWhiteSpace(code))
                    {
                        continue;
                    }

                    if (!MarketDataParsingHelpers.TryParsePrice(cells[closeIndex], out var closePrice))
                    {
                        continue;
                    }

                    quotes.Add(new OfficialPriceQuote(code, name, closePrice));
                }
            }

            var explicitNoData = sourceDate is null && quotes.Count == 0;
            return new TpexDailyCloseParseResult(sourceDate, explicitNoData, quotes);
        }
    }
}
