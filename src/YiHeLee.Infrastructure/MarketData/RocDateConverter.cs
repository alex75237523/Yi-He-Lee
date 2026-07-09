using System.Globalization;

namespace YiHeLee.Infrastructure.MarketData;

/// <summary>
/// 西元／民國年日期轉換工具。TWSE 使用西元 yyyyMMdd，TPEx 舊版行情查詢使用民國年 yyy/MM/dd。
/// 集中於此避免在多個 Provider／Parser 各自重複轉換邏輯。
/// </summary>
public static class RocDateConverter
{
    private const int RocEpochOffsetYears = 1911;

    /// <summary>轉成 TWSE 查詢用的西元緊湊格式，例如 2026-07-09 -> "20260709"。</summary>
    public static string ToWesternCompact(DateOnly date) => date.ToString("yyyyMMdd", CultureInfo.InvariantCulture);

    /// <summary>轉成 TPEx 查詢用的民國年斜線格式，例如 2026-07-09 -> "115/07/09"。</summary>
    public static string ToRocSlash(DateOnly date)
        => $"{date.Year - RocEpochOffsetYears}/{date.Month:D2}/{date.Day:D2}";

    /// <summary>解析西元緊湊格式（"20260709"）；失敗回傳 false，不丟例外，交由呼叫端決定如何處理。</summary>
    public static bool TryParseWesternCompact(string? value, out DateOnly date)
    {
        date = default;
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        return DateOnly.TryParseExact(value.Trim(), "yyyyMMdd", CultureInfo.InvariantCulture, DateTimeStyles.None, out date);
    }

    /// <summary>解析民國年緊湊格式（"1150709"，年 3 碼＋月 2 碼＋日 2 碼）。</summary>
    public static bool TryParseRocCompact(string? value, out DateOnly date)
    {
        date = default;
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        var text = value.Trim();
        if (text.Length != 7 || !int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out _))
        {
            return false;
        }

        if (!int.TryParse(text[..3], NumberStyles.None, CultureInfo.InvariantCulture, out var rocYear)
            || !int.TryParse(text.Substring(3, 2), NumberStyles.None, CultureInfo.InvariantCulture, out var month)
            || !int.TryParse(text.Substring(5, 2), NumberStyles.None, CultureInfo.InvariantCulture, out var day))
        {
            return false;
        }

        try
        {
            date = new DateOnly(rocYear + RocEpochOffsetYears, month, day);
            return true;
        }
        catch (ArgumentOutOfRangeException)
        {
            return false;
        }
    }

    /// <summary>解析民國年斜線格式（"115/07/09"）。</summary>
    public static bool TryParseRocSlash(string? value, out DateOnly date)
    {
        date = default;
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        var parts = value.Trim().Split('/');
        if (parts.Length != 3
            || !int.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out var rocYear)
            || !int.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out var month)
            || !int.TryParse(parts[2], NumberStyles.None, CultureInfo.InvariantCulture, out var day))
        {
            return false;
        }

        try
        {
            date = new DateOnly(rocYear + RocEpochOffsetYears, month, day);
            return true;
        }
        catch (ArgumentOutOfRangeException)
        {
            return false;
        }
    }
}
