using System.Text.RegularExpressions;

namespace YiHeLee.Domain;

/// <summary>
/// 股票代碼正規化：統一去除空白、轉大寫。所有來源（Excel、TWSE、TPEx、TPEx 興櫃、SQLite、
/// 均線計算、策略比對、Excel 輸出）都必須呼叫本類別，禁止各自重複實作一套 Trim/ToUpper 規則。
/// 本類別不含前導零推測，補零必須經 <see cref="StockIdentityResolver"/> 與官方主檔確認。
/// </summary>
public static partial class StockCodeNormalizer
{
    public static string Normalize(string? value)
        => WhitespacePattern().Replace(value ?? string.Empty, string.Empty).ToUpperInvariant();

    [GeneratedRegex(@"\s+", RegexOptions.CultureInvariant)]
    private static partial Regex WhitespacePattern();
}

/// <summary>台股商品分類，用於決定是否納入均線策略。</summary>
public enum SecurityProductType
{
    /// <summary>格式不符合任何已知台股商品規則（例如純數字金額）。</summary>
    Unknown = 0,

    /// <summary>一般上市／上櫃／興櫃股票（4 碼數字）。</summary>
    Ordinary = 1,

    /// <summary>一般 ETF（00 開頭 4 碼或 5 碼數字，無字尾）。</summary>
    Etf = 2,

    /// <summary>槓桿／反向 ETF（00 開頭 5 碼數字＋L／R 字尾）。</summary>
    LeveragedOrInverseEtf = 3,

    /// <summary>債券 ETF（00 開頭 5 碼數字＋B 字尾）。</summary>
    BondEtf = 4,

    /// <summary>主動式 ETF（00 開頭 5 碼數字＋其他單一英文字尾）。</summary>
    ActiveEtf = 5,

    /// <summary>存託憑證 DR（91 開頭 6 碼數字）。</summary>
    DepositoryReceipt = 6,

    /// <summary>權證（6 碼數字，非 DR 格式）。短期衍生性商品，明確排除於均線策略之外。</summary>
    Warrant = 7
}

/// <summary>
/// 股票代碼格式分類與均線策略適用性判斷結果。<see cref="IsFormatValid"/> 僅代表格式符合已知台股商品規則，
/// 不代表官方主檔真的存在此代碼；是否真實存在必須另外以官方股票主檔或當日官方行情驗證
/// （見 <c>StockIdentityResolutionService</c>）。
/// </summary>
public sealed record StockIdentity(
    string NormalizedCode,
    SecurityProductType ProductType,
    bool IsFormatValid,
    bool IsEligibleForMovingAverageStrategy,
    string? IneligibleReason);

/// <summary>
/// 依台灣證券市場實際商品代碼格式判斷股票代碼類別，取代過去單純「4～10 碼英數字」的過寬規則
/// （該規則會誤把 8 位數金額，例如 10037677，當成股票代碼）。不得僅靠字串長度判斷；
/// 權證屬短期衍生性商品，價格行為與均線策略無關，依需求明確排除於均線策略之外並留下原因。
/// </summary>
public static partial class StockIdentityResolver
{
    public static StockIdentity Resolve(string normalizedCode)
    {
        if (string.IsNullOrWhiteSpace(normalizedCode))
        {
            return new StockIdentity(normalizedCode ?? string.Empty, SecurityProductType.Unknown, false, false, "股票代碼為空白。");
        }

        if (OrdinaryOrEtf4Pattern().IsMatch(normalizedCode))
        {
            var type = normalizedCode.StartsWith("00", StringComparison.Ordinal) ? SecurityProductType.Etf : SecurityProductType.Ordinary;
            return new StockIdentity(normalizedCode, type, true, true, null);
        }

        var etfMatch = Etf5Pattern().Match(normalizedCode);
        if (etfMatch.Success)
        {
            var suffix = etfMatch.Groups[1].Success ? etfMatch.Groups[1].Value : null;
            var productType = suffix switch
            {
                null => SecurityProductType.Etf,
                "L" or "R" => SecurityProductType.LeveragedOrInverseEtf,
                "B" => SecurityProductType.BondEtf,
                _ => SecurityProductType.ActiveEtf
            };
            return new StockIdentity(normalizedCode, productType, true, true, null);
        }

        if (DepositoryReceiptPattern().IsMatch(normalizedCode))
        {
            return new StockIdentity(normalizedCode, SecurityProductType.DepositoryReceipt, true, true, null);
        }

        if (WarrantPattern().IsMatch(normalizedCode))
        {
            return new StockIdentity(
                normalizedCode, SecurityProductType.Warrant, true, false,
                "權證為短期衍生性商品，價格行為與長期均線策略無關，依需求明確排除於均線策略之外，非程式錯誤。");
        }

        return new StockIdentity(
            normalizedCode, SecurityProductType.Unknown, false, false,
            "股票代碼格式不符合台灣上市、上櫃、興櫃、ETF、DR 或權證等已知商品格式，可能為金額、權益數或其他非股票資料。");
    }

    // 一般上市／上櫃／興櫃股票，或 4 碼 ETF（0050、0056）。
    [GeneratedRegex(@"^\d{4}$", RegexOptions.CultureInvariant)]
    private static partial Regex OrdinaryOrEtf4Pattern();

    // 5 碼 ETF：00 開頭 3 碼數字，選用單一英文字尾（L／R 槓桿反向、B 債券、其他為主動式）。
    [GeneratedRegex(@"^00\d{3}([A-Z])?$", RegexOptions.CultureInvariant)]
    private static partial Regex Etf5Pattern();

    // 存託憑證 DR：91 開頭 6 碼數字。
    [GeneratedRegex(@"^91\d{4}$", RegexOptions.CultureInvariant)]
    private static partial Regex DepositoryReceiptPattern();

    // 權證：6 碼數字（非 91 開頭）。
    [GeneratedRegex(@"^\d{6}$", RegexOptions.CultureInvariant)]
    private static partial Regex WarrantPattern();
}
