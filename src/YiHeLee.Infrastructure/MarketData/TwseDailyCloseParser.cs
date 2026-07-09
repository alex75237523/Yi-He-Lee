using System.Text.Json;
using YiHeLee.Application.Exceptions;
using YiHeLee.Domain;

namespace YiHeLee.Infrastructure.MarketData;

/// <summary>解析結果：Provider 只負責回報，是否可寫入正式資料表由 Service 依 SourceDataDate 與 targetDate 比對決定。</summary>
public sealed record TwseDailyCloseParseResult(
    DateOnly? SourceDataDate,
    bool IsExplicitNoData,
    IReadOnlyList<OfficialPriceQuote> Quotes);

/// <summary>
/// 將 TWSE 官方「每日收盤行情-大盤統計資訊」(MI_INDEX, type=ALLBUT0999) 回應轉成結構化資料。
/// 不負責網路存取，方便離線以去識別化 Fixture 單元測試。
/// </summary>
public static class TwseDailyCloseParser
{
    private const string NoDataStat = "很抱歉，沒有符合條件的資料";

    public static TwseDailyCloseParseResult Parse(string rawJson)
    {
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(rawJson);
        }
        catch (JsonException ex)
        {
            throw new RetryableJobException("TWSE 官方回應不是有效 JSON，可能為錯誤頁或驗證頁。", ex);
        }

        using (document)
        {
            var root = document.RootElement;

            var stat = root.TryGetProperty("stat", out var statElement) ? statElement.GetString() : null;
            if (!string.IsNullOrEmpty(stat) && stat.Contains(NoDataStat, StringComparison.Ordinal))
            {
                // TWSE 對非交易日（休市、例假日）明確回報「沒有符合條件的資料」，視為合法零筆。
                return new TwseDailyCloseParseResult(null, true, []);
            }

            if (!root.TryGetProperty("date", out var dateElement) || dateElement.ValueKind != JsonValueKind.String)
            {
                throw new RetryableJobException("TWSE 回應缺少 date 欄位，無法驗證資料日期，拒絕視為成功。");
            }

            if (!RocDateConverter.TryParseWesternCompact(dateElement.GetString(), out var sourceDate))
            {
                throw new RetryableJobException($"TWSE 回應 date 欄位格式無法解析：{dateElement.GetString()}");
            }

            if (!root.TryGetProperty("tables", out var tablesElement) || tablesElement.ValueKind != JsonValueKind.Array)
            {
                throw new RetryableJobException("TWSE 回應缺少 tables 陣列，網站結構可能已變更。");
            }

            JsonElement? targetTable = null;
            foreach (var table in tablesElement.EnumerateArray())
            {
                if (!table.TryGetProperty("fields", out var fieldsElement) || fieldsElement.ValueKind != JsonValueKind.Array)
                {
                    continue;
                }

                var fields = fieldsElement.EnumerateArray().Select(x => x.GetString() ?? string.Empty).ToArray();
                if (fields.Contains("證券代號") && fields.Contains("收盤價"))
                {
                    targetTable = table;
                    break;
                }
            }

            if (targetTable is null)
            {
                throw new RetryableJobException("TWSE 回應找不到包含證券代號、收盤價的每日收盤行情表格，網站結構可能已變更。");
            }

            var fieldNames = targetTable.Value.GetProperty("fields").EnumerateArray().Select(x => x.GetString() ?? string.Empty).ToArray();
            var codeIndex = Array.IndexOf(fieldNames, "證券代號");
            var nameIndex = Array.IndexOf(fieldNames, "證券名稱");
            var closeIndex = Array.IndexOf(fieldNames, "收盤價");
            if (codeIndex < 0 || nameIndex < 0 || closeIndex < 0)
            {
                throw new RetryableJobException("TWSE 每日收盤行情表格欄位不完整，網站結構可能已變更。");
            }

            var quotes = new List<OfficialPriceQuote>();
            if (targetTable.Value.TryGetProperty("data", out var dataElement) && dataElement.ValueKind == JsonValueKind.Array)
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

                    // 個股當日暫停交易、無成交等情形收盤價會顯示「--」，略過該列但不影響整批其餘資料。
                    if (!MarketDataParsingHelpers.TryParsePrice(cells[closeIndex], out var closePrice))
                    {
                        continue;
                    }

                    quotes.Add(new OfficialPriceQuote(code, name, closePrice));
                }
            }

            return new TwseDailyCloseParseResult(sourceDate, false, quotes);
        }
    }
}
