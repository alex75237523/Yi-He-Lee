using System.Globalization;

namespace YiHeLee.Infrastructure.Excel;

/// <summary>
/// 「進場價/平均價」儲存格解析結果。Price 有值代表可用；否則 Issue 說明無法判讀的原因（繁體中文，直接顯示給使用者）。
/// </summary>
public sealed record EntryAveragePriceParseResult(decimal? Price, string? Issue)
{
    public bool IsValid => Price is not null;

    public static EntryAveragePriceParseResult Valid(decimal price) => new(price, null);
    public static EntryAveragePriceParseResult Invalid(string issue) => new(null, issue);
}

/// <summary>
/// 解析 Excel「進場價/平均價」欄位的儲存格值。此欄位<b>不是</b> DDE 欄位，只是一般手動或公式輸入的成本價，
/// 因此錯誤訊息不得寫成 DDE 異常；但儲存格仍可能是 Excel 錯誤值（例如公式參照到已刪除的儲存格）、
/// 空白、0、負數或無法解析的文字，一律不得當成價格參與均線判斷，必須回報明確原因。
/// 與 <see cref="CurrentPriceCellParser"/> 使用相同的安全數值解析規則（千分位、全形／半形數字皆可解析），
/// 但訊息內容完全獨立，不得與「現價」欄位的原因混用或互相代替。
/// </summary>
public static class EntryAveragePriceCellParser
{
    // COM VT_ERROR 封送為 Int32，值為 0x800A0000 | Excel 錯誤代碼（例如 #N/A = 2042 → 0x800A07FA）。
    private const long CvErrMask = 0xFFFF0000;
    private const long CvErrBase = 0x800A0000;

    public static EntryAveragePriceParseResult Parse(object? value)
    {
        switch (value)
        {
            case null:
                return EntryAveragePriceParseResult.Invalid("儲存格為空白，無法讀取進場價/平均價");
            case int intValue when IsExcelError(intValue):
                return EntryAveragePriceParseResult.Invalid(DescribeExcelError(intValue));
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
                    return EntryAveragePriceParseResult.Invalid("儲存格為空白，無法讀取進場價/平均價");
                }

                var normalized = text.Replace(",", string.Empty, StringComparison.Ordinal);
                if (decimal.TryParse(normalized, NumberStyles.Number | NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var parsed)
                    || decimal.TryParse(normalized, NumberStyles.Number | NumberStyles.AllowLeadingSign, CultureInfo.GetCultureInfo("zh-TW"), out parsed))
                {
                    return ValidatePositive(parsed);
                }

                return EntryAveragePriceParseResult.Invalid($"內容「{text}」無法解析為數字，非有效的進場價/平均價");
        }
    }

    private static EntryAveragePriceParseResult ValidatePositive(decimal price)
        => price > 0
            ? EntryAveragePriceParseResult.Valid(price)
            : EntryAveragePriceParseResult.Invalid($"數值為 {price.ToString("0.####", CultureInfo.InvariantCulture)}（非正數），非有效的進場價/平均價");

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
            2029 => "#NAME?（公式或參照名稱錯誤）",
            2036 => "#NUM!",
            2042 => "#N/A（找不到對應資料）",
            2043 => "#GETTING_DATA（資料計算中，尚未取得數值）",
            2045 => "#SPILL!",
            2046 => "#CONNECT!（外部連線失敗）",
            2047 => "#BLOCKED!（外部連線被封鎖）",
            2048 => "#UNKNOWN!",
            2049 => "#FIELD!",
            2050 => "#CALC!",
            _ => $"Excel 錯誤值（代碼 {code}）"
        };
        return $"儲存格為 {name}，非有效的進場價/平均價（非 DDE 欄位）";
    }
}
