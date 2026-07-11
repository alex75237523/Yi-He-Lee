using System.Text.RegularExpressions;
using System.Text.Json;
using YiHeLee.Application.Exceptions;
using YiHeLee.Domain;

namespace YiHeLee.Infrastructure.MarketData;

public sealed record EsbHistoricalDailyCloseParseResult(
    IReadOnlyList<EsbHistoricalDailyQuote> Quotes);

public sealed record EsbHistoricalDailyQuote(
    DateOnly TradeDate,
    OfficialPriceQuote Quote);

/// <summary>
/// 解析 TPEx 興櫃個股歷史行情。官方歷史頁只提供「個股＋月份」查詢，且欄位沒有收盤價；
/// 這裡依官方表格的「成交均價」作為興櫃歷史價格，用於補足均線所需的有效交易日。
/// </summary>
public static partial class EsbHistoricalDailyCloseParser
{
    public static EsbHistoricalDailyCloseParseResult Parse(string rawJson, string requestedStockCode)
    {
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(rawJson);
        }
        catch (JsonException ex)
        {
            throw new RetryableJobException("TPEx 興櫃個股歷史行情回應不是合法 JSON，可能為暫時性錯誤頁。", ex);
        }

        using (document)
        {
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                throw new RetryableJobException("TPEx 興櫃個股歷史行情回應格式不是物件。");
            }

            if (!root.TryGetProperty("tables", out var tablesElement)
                || tablesElement.ValueKind != JsonValueKind.Array
                || tablesElement.GetArrayLength() == 0)
            {
                return new EsbHistoricalDailyCloseParseResult([]);
            }

            var table = tablesElement[0];
            if (!table.TryGetProperty("fields", out var fieldsElement) || fieldsElement.ValueKind != JsonValueKind.Array)
            {
                throw new RetryableJobException("TPEx 興櫃個股歷史行情缺少欄位定義。");
            }

            var fieldNames = fieldsElement.EnumerateArray()
                .Select(x => MarketDataParsingHelpers.Normalize(x.GetString()))
                .ToArray();
            var dateIndex = Array.IndexOf(fieldNames, "日期");
            var averageIndex = Array.IndexOf(fieldNames, "成交均價");
            if (dateIndex < 0 || averageIndex < 0)
            {
                throw new RetryableJobException("TPEx 興櫃個股歷史行情缺少日期或成交均價欄位。");
            }

            var (stockCode, stockName) = ParseStockIdentity(table, requestedStockCode);
            var quotes = new List<EsbHistoricalDailyQuote>();
            if (table.TryGetProperty("data", out var dataElement) && dataElement.ValueKind == JsonValueKind.Array)
            {
                foreach (var row in dataElement.EnumerateArray())
                {
                    if (row.ValueKind != JsonValueKind.Array)
                    {
                        continue;
                    }

                    var cells = row.EnumerateArray().Select(x => x.GetString() ?? string.Empty).ToArray();
                    if (cells.Length <= Math.Max(dateIndex, averageIndex))
                    {
                        continue;
                    }

                    if (!RocDateConverter.TryParseRocSlash(cells[dateIndex], out var tradeDate))
                    {
                        continue;
                    }

                    if (!MarketDataParsingHelpers.TryParsePrice(cells[averageIndex], out var averagePrice))
                    {
                        continue;
                    }

                    quotes.Add(new EsbHistoricalDailyQuote(
                        tradeDate,
                        new OfficialPriceQuote(stockCode, stockName, averagePrice)));
                }
            }

            return new EsbHistoricalDailyCloseParseResult(quotes);
        }
    }

    private static (string StockCode, string StockName) ParseStockIdentity(JsonElement table, string requestedStockCode)
    {
        var requested = MarketDataParsingHelpers.Normalize(requestedStockCode);
        if (table.TryGetProperty("subtitle", out var subtitleElement)
            && subtitleElement.ValueKind == JsonValueKind.String)
        {
            var subtitle = MarketDataParsingHelpers.Normalize(subtitleElement.GetString());
            var match = SubtitleIdentityRegex().Match(subtitle);
            if (match.Success)
            {
                return (match.Groups["code"].Value, match.Groups["name"].Value.Trim());
            }
        }

        return (requested, requested);
    }

    [GeneratedRegex(@"^\S+\s+(?<code>[0-9A-Za-z]+)\s+(?<name>.+)$")]
    private static partial Regex SubtitleIdentityRegex();
}
