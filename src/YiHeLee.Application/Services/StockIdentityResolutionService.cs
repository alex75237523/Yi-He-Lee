using YiHeLee.Application.Abstractions;
using YiHeLee.Domain;

namespace YiHeLee.Application.Services;

/// <summary>
/// 統一協調 Excel 持股代碼的正規化、前導零回復與官方股票主檔身分驗證，是全系統唯一負責
/// 「代碼是否可信、是否需要補零」判斷的地方，避免 Excel、TWSE、TPEx、SQLite、均線計算、
/// 策略比對、Excel 輸出等多處各自複製一套規則或各自補前導零。
/// 補零一律只是「嘗試」：只有補零後的代碼確實存在於官方主檔（今日已出現在 TWSE／TPEx／TPEx興櫃
/// 完整行情中）才會採用，不得盲目將所有短數字補零。
/// </summary>
public sealed class StockIdentityResolutionService
{
    private readonly IMarketDataRepository _repository;

    public StockIdentityResolutionService(IMarketDataRepository repository)
    {
        _repository = repository;
    }

    /// <summary>
    /// 解析一批原始股票代碼（Excel 讀到的顯示文字）。回傳字典以「正規化後的原始代碼」為鍵，
    /// 每筆代碼恰好一筆解析結果，供呼叫端逐筆判斷是否可納入均線策略、應使用哪個代碼查詢／計算。
    /// </summary>
    public async Task<IReadOnlyDictionary<string, StockCodeResolution>> ResolveAsync(
        IReadOnlyCollection<string> rawStockCodes,
        CancellationToken cancellationToken)
    {
        var normalizedCodes = rawStockCodes
            .Select(StockCodeNormalizer.Normalize)
            .Where(code => !string.IsNullOrWhiteSpace(code))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        var result = new Dictionary<string, StockCodeResolution>(StringComparer.OrdinalIgnoreCase);
        if (normalizedCodes.Length == 0)
        {
            return result;
        }

        // 第一步：直接以原始代碼查官方主檔（今日已出現在 TWSE／TPEx／TPEx興櫃行情中的代碼）。
        var directMarketTypes = await _repository.GetStockMarketTypesAsync(normalizedCodes, cancellationToken).ConfigureAwait(false);

        // 第二步：尚未直接命中、且為 1～3 碼純數字（例如 Excel 把 0050 讀成數字 50 導致前導零遺失）的代碼，
        // 嘗試補零為 4 碼／5 碼後再查一次官方主檔；補零前必須經官方主檔確認存在，不得盲目補零。
        var paddingCandidatesByRawCode = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase);
        var allPaddingCandidates = new List<string>();
        foreach (var code in normalizedCodes)
        {
            if (directMarketTypes.ContainsKey(code) || !IsShortNumericCandidate(code))
            {
                continue;
            }

            var candidates = BuildZeroPaddedCandidates(code);
            paddingCandidatesByRawCode[code] = candidates;
            allPaddingCandidates.AddRange(candidates);
        }

        var paddedMarketTypes = allPaddingCandidates.Count == 0
            ? new Dictionary<string, MarketType>(StringComparer.OrdinalIgnoreCase)
            : await _repository.GetStockMarketTypesAsync(
                allPaddingCandidates.Distinct(StringComparer.OrdinalIgnoreCase).ToArray(), cancellationToken).ConfigureAwait(false);

        foreach (var rawCode in normalizedCodes)
        {
            if (directMarketTypes.TryGetValue(rawCode, out var marketType))
            {
                var identity = StockIdentityResolver.Resolve(rawCode);
                result[rawCode] = new StockCodeResolution(rawCode, rawCode, marketType, identity, true, null);
                continue;
            }

            if (paddingCandidatesByRawCode.TryGetValue(rawCode, out var candidates))
            {
                var matchedCandidate = candidates.FirstOrDefault(candidate => paddedMarketTypes.ContainsKey(candidate));
                if (matchedCandidate is not null)
                {
                    var identity = StockIdentityResolver.Resolve(matchedCandidate);
                    result[rawCode] = new StockCodeResolution(
                        rawCode, matchedCandidate, paddedMarketTypes[matchedCandidate], identity, true, null);
                    continue;
                }
            }

            var fallbackIdentity = StockIdentityResolver.Resolve(rawCode);
            var reason = fallbackIdentity.IsFormatValid
                ? "股票代碼格式有效，但官方股票主檔／當日官方行情尚查無此代碼，可能為代碼有誤或當日尚未取得官方資料。"
                : (fallbackIdentity.IneligibleReason ?? "股票代碼無法識別。");
            result[rawCode] = new StockCodeResolution(rawCode, rawCode, null, fallbackIdentity, false, reason);
        }

        return result;
    }

    /// <summary>1～3 碼純數字才是補零候選；4 碼以上或含英文字尾者不做前導零猜測，交由格式規則直接判斷。</summary>
    private static bool IsShortNumericCandidate(string code)
        => code.Length is >= 1 and <= 3 && code.All(char.IsAsciiDigit);

    private static string[] BuildZeroPaddedCandidates(string code) =>
    [
        code.PadLeft(4, '0'),
        code.PadLeft(5, '0')
    ];
}
