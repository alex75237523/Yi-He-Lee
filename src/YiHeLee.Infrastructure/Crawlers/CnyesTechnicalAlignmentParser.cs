using System.Globalization;
using System.Text.RegularExpressions;
using YiHeLee.Application.Exceptions;
using YiHeLee.Domain;

namespace YiHeLee.Infrastructure.Crawlers;

/// <summary>
/// 將鉅亨網技術指標表格轉成結構化資料。
/// Parser 不負責網路存取，方便在網站欄位變動時單獨測試與維護。
/// </summary>
public sealed partial class CnyesTechnicalAlignmentParser
{
    public DateOnly ExtractDisplayedDate(string contextText)
    {
        var matches = DateRegex().Matches(contextText ?? string.Empty);
        if (matches.Count == 0)
        {
            throw new RetryableJobException("表格附近找不到頁面實際顯示日期。");
        }

        // 頁面日期連結可能同時包含多日，表格附近最後一個日期視為目前顯示日期。
        var value = matches[^1].Value;
        return DateOnly.ParseExact(value, "yyyy-MM-dd", CultureInfo.InvariantCulture);
    }

    public IReadOnlyList<TechnicalIndicator> ParseRows(
        IReadOnlyList<string[]> rows,
        SourceDefinition source,
        MarketType marketType,
        DateOnly pageDate,
        DateTimeOffset startedAt,
        DateTimeOffset completedAt)
    {
        var result = new List<TechnicalIndicator>();
        Dictionary<string, int>? columns = null;

        foreach (var rawRow in rows)
        {
            var row = rawRow.Select(NormalizeText).ToArray();
            if (columns is null && IsHeaderRow(row))
            {
                columns = BuildColumnMap(row);
                continue;
            }

            if (columns is null || row.Length == 0)
            {
                continue;
            }

            var code = Get(row, columns, "代碼");
            if (string.IsNullOrWhiteSpace(code) || !StockCodeRegex().IsMatch(code))
            {
                continue;
            }

            var name = Get(row, columns, "名稱");
            var close = ParseDecimal(Get(row, columns, "收盤價"), "收盤價", code);
            var ma5 = ParseDecimal(Get(row, columns, "5日均價"), "5日均價", code);
            var ma20 = ParseDecimal(Get(row, columns, "20日均價"), "20日均價", code);
            var ma60 = ParseDecimal(Get(row, columns, "60日均價"), "60日均價", code);
            var ma120 = ParseDecimal(Get(row, columns, "120日均價"), "120日均價", code);

            if (string.IsNullOrWhiteSpace(name))
            {
                throw new RetryableJobException($"股票 {code} 缺少名稱。");
            }

            result.Add(new TechnicalIndicator(
                pageDate,
                source.IndicatorType,
                marketType,
                code,
                name,
                close,
                ma5,
                ma20,
                ma60,
                ma120,
                source.Url.ToString(),
                startedAt,
                completedAt));
        }

        if (columns is null)
        {
            throw new RetryableJobException("表格找不到完整表頭，網站欄位可能已變更。");
        }

        return result;
    }

    private static bool IsHeaderRow(IReadOnlyCollection<string> row)
    {
        var normalized = row.Select(CanonicalizeHeader)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        return new[] { "代碼", "名稱", "收盤價", "5日均價", "20日均價", "60日均價", "120日均價" }
            .All(normalized.Contains);
    }

    private static Dictionary<string, int> BuildColumnMap(IReadOnlyList<string> row)
    {
        var map = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < row.Count; i++)
        {
            var key = CanonicalizeHeader(row[i]);
            if (!string.IsNullOrWhiteSpace(key))
            {
                map[key] = i;
            }
        }

        return map;
    }

    private static string CanonicalizeHeader(string value)
    {
        var normalized = value.Replace(" ", string.Empty, StringComparison.Ordinal).Trim();
        return normalized switch
        {
            "股票代碼" or "股票編號" => "代碼",
            "股票名稱" or "股名" => "名稱",
            _ => normalized
        };
    }

    private static string Get(IReadOnlyList<string> row, IReadOnlyDictionary<string, int> columns, string name)
        => columns.TryGetValue(name, out var index) && index < row.Count ? row[index] : string.Empty;

    private static decimal ParseDecimal(string value, string columnName, string stockCode)
    {
        var normalized = value.Replace(",", string.Empty, StringComparison.Ordinal).Trim();
        if (!decimal.TryParse(normalized, NumberStyles.Number | NumberStyles.AllowLeadingSign,
                CultureInfo.InvariantCulture, out var parsed))
        {
            throw new RetryableJobException($"股票 {stockCode} 的 {columnName}「{value}」無法轉為數字。");
        }

        return parsed;
    }

    private static string NormalizeText(string value)
        => Regex.Replace(value ?? string.Empty, @"\s+", " ").Trim();

    [GeneratedRegex(@"\b20\d{2}-\d{2}-\d{2}\b", RegexOptions.CultureInvariant)]
    private static partial Regex DateRegex();

    [GeneratedRegex(@"^[0-9A-Z]{4,10}$", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase)]
    private static partial Regex StockCodeRegex();
}
