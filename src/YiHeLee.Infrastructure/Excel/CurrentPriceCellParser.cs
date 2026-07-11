using System.Globalization;

namespace YiHeLee.Infrastructure.Excel;

/// <summary>
/// 「現價」儲存格解析結果。Price 有值代表可用；否則 Issue 說明無法判讀的原因（繁體中文，直接顯示給使用者）。
/// </summary>
public sealed record CurrentPriceParseResult(decimal? Price, string? Issue)
{
    public bool IsValid => Price is not null;

    public static CurrentPriceParseResult Valid(decimal price) => new(price, null);
    public static CurrentPriceParseResult Invalid(string issue) => new(null, issue);
}

/// <summary>
/// 解析 Excel「現價」欄位的儲存格值。此欄位串接外部 DDE（看盤軟體），不能假設一定是正常數字：
/// DDE 未連線或看盤軟體未開啟時，Value2 會回傳 Excel 錯誤值（COM VT_ERROR，封送為 0x800Axxxx 的 Int32）、
/// 空白、0 或文字，一律不得當成價格參與均線判斷，必須回報明確原因。
/// </summary>
public static class CurrentPriceCellParser
{
    // COM VT_ERROR 封送為 Int32，值為 0x800A0000 | Excel 錯誤代碼（例如 #N/A = 2042 → 0x800A07FA）。
    private const long CvErrMask = 0xFFFF0000;
    private const long CvErrBase = 0x800A0000;

    public static CurrentPriceParseResult Parse(object? value)
    {
        switch (value)
        {
            case null:
                return CurrentPriceParseResult.Invalid("儲存格為空白，DDE 可能尚未回傳資料");
            case int intValue when IsExcelError(intValue):
                return CurrentPriceParseResult.Invalid(DescribeExcelError(intValue));
            case decimal decimalValue:
                return ValidatePositive(decimalValue);
            case double doubleValue:
                return ValidatePositive(Convert.ToDecimal(doubleValue, CultureInfo.InvariantCulture));
            case float floatValue:
                return ValidatePositive(Convert.ToDecimal(floatValue, CultureInfo.InvariantCulture));
            case int intValue:
                return ValidatePositive(intValue);
            case long longValue:
                return ValidatePositive(longValue);
            default:
                var text = Convert.ToString(value, CultureInfo.CurrentCulture)?.Trim() ?? string.Empty;
                if (text.Length == 0)
                {
                    return CurrentPriceParseResult.Invalid("儲存格為空白，DDE 可能尚未回傳資料");
                }

                var normalized = text.Replace(",", string.Empty, StringComparison.Ordinal);
                if (decimal.TryParse(normalized, NumberStyles.Number | NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var parsed)
                    || decimal.TryParse(normalized, NumberStyles.Number | NumberStyles.AllowLeadingSign, CultureInfo.GetCultureInfo("zh-TW"), out parsed))
                {
                    return ValidatePositive(parsed);
                }

                return CurrentPriceParseResult.Invalid($"內容「{text}」無法解析為數字，DDE 可能回傳狀態文字");
        }
    }

    private static CurrentPriceParseResult ValidatePositive(decimal price)
        => price > 0
            ? CurrentPriceParseResult.Valid(price)
            : CurrentPriceParseResult.Invalid($"數值為 {price.ToString("0.####", CultureInfo.InvariantCulture)}（非正數），DDE 可能未連線");

    private static bool IsExcelError(int value)
        => ((uint)value & CvErrMask) == CvErrBase;

    private static string DescribeExcelError(int value)
    {
        var code = (uint)value & 0xFFFF;
        var name = code switch
        {
            2000 => "#NULL!",
            2007 => "#DIV/0!",
            2015 => "#VALUE!",
            2023 => "#REF!",
            2029 => "#NAME?（Excel 不認得 DDE 函數，看盤軟體可能未開啟）",
            2036 => "#NUM!",
            2042 => "#N/A（DDE 尚未取得資料，看盤軟體可能未開啟或未連線）",
            2043 => "#GETTING_DATA（外部資料擷取中，尚未取得數值）",
            2045 => "#SPILL!",
            2046 => "#CONNECT!（外部連線失敗，請確認看盤軟體與網路連線）",
            2047 => "#BLOCKED!（Excel 封鎖了外部 DDE 連線；請開啟看盤軟體，並在 Excel「啟用內容」或信任中心允許外部內容後重新整理）",
            2048 => "#UNKNOWN!",
            2049 => "#FIELD!",
            2050 => "#CALC!",
            _ => $"Excel 錯誤值（代碼 {code}）"
        };
        return $"儲存格為 {name}";
    }
}
