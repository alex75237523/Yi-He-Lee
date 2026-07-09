using System.Globalization;

namespace YiHeLee.Infrastructure.MarketData;

/// <summary>TWSE／TPEx 官方回應共用的數值解析規則：需處理千分位逗號、前後空白，以及「--」「N/A」等無成交標記。</summary>
public static class MarketDataParsingHelpers
{
    private static readonly string[] NoTradeMarkers = ["--", "---", "N/A", "NA", "-", ""];

    /// <summary>嘗試解析收盤價；回傳 false 代表該列當日無有效成交價（例如暫停交易），呼叫端應略過而非整批失敗。</summary>
    public static bool TryParsePrice(string? raw, out decimal value)
    {
        value = 0m;
        if (raw is null)
        {
            return false;
        }

        var normalized = raw.Replace(",", string.Empty, StringComparison.Ordinal).Trim();
        if (NoTradeMarkers.Contains(normalized, StringComparer.OrdinalIgnoreCase))
        {
            return false;
        }

        return decimal.TryParse(normalized, NumberStyles.Number | NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out value);
    }

    public static string Normalize(string? raw) => (raw ?? string.Empty).Trim();
}
