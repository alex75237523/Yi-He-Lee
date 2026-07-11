using YiHeLee.Domain;

namespace YiHeLee.Application.Services;

/// <summary>
/// 均線策略判斷。正式 MA5／MA20／MA120 一律來自本系統依 TWSE／TPEx 官方收盤價自行計算的
/// <see cref="MovingAverageResult"/>；鉅亨網多頭／空頭排列僅作交叉驗證與清單保存，不再是正式均價來源。
/// </summary>
public sealed class StrategyEvaluationService
{
    /// <summary>
    /// 依需求逐項判斷 MA5、MA20、MA120 是否小於或等於 Excel「現價」欄位（外部 DDE）；任一條件成立即產生通知。
    /// MA60 僅保存與顯示，不參與觸發。任一均線因交易日數不足而為 null 時，該項不得觸發、也不得硬算。
    /// 現價因 DDE 錯誤值、空白、0 或無法解析而無效時，必須產生「現價異常」通知告知使用者，不得靜默略過，
    /// 也不得以任何其他價格（收盤價、昨日現價）代替判斷。
    /// </summary>
    public IReadOnlyList<StrategyAlert> Evaluate(
        DateOnly tradeDate,
        IReadOnlyList<CustomerHolding> holdings,
        IReadOnlyList<MovingAverageResult> movingAverages,
        IReadOnlyDictionary<string, MarketType> marketTypesByStockCode,
        DateTimeOffset calculatedAt,
        IReadOnlyDictionary<string, StockCodeResolution>? resolutions = null)
    {
        var byCode = movingAverages
            .GroupBy(x => NormalizeStockCode(x.StockCode), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.OrdinalIgnoreCase);

        var result = new List<StrategyAlert>(holdings.Count);

        foreach (var holding in holdings)
        {
            var rawCode = NormalizeStockCode(holding.StockCode);
            var resolution = resolutions is not null && resolutions.TryGetValue(rawCode, out var r) ? r : null;

            // 逐檔股票身分驗證優先於現價／均線判斷：代碼無法識別（例如 8 位數金額誤判、格式不符）
            // 或明確不納入均線策略（例如權證）時，直接產生診斷通知，不得繼續用未經確認的代碼查詢均線或誤算。
            if (resolution is not null && (!resolution.IsRecognized || !resolution.Identity.IsEligibleForMovingAverageStrategy))
            {
                var identityDiagnosticStatus = !resolution.IsRecognized ? "股票代碼無法識別" : "非策略商品";
                var reasonText = resolution.UnrecognizedReason ?? resolution.Identity.IneligibleReason ?? "股票代碼無法識別或非策略商品。";
                result.Add(new StrategyAlert(
                    tradeDate,
                    AlertKind.TechnicalIndicatorMissing,
                    holding.WorkbookPath,
                    holding.SheetName,
                    holding.CustomerName,
                    holding.ExcelRow,
                    holding.StockCode,
                    holding.StockName,
                    holding.CurrentPrice,
                    holding.Quantity,
                    null, null, null, null, null,
                    false, false, false,
                    reasonText,
                    null,
                    null,
                    null,
                    null,
                    calculatedAt,
                    identityDiagnosticStatus,
                    reasonText,
                    0,
                    null));
                continue;
            }

            var code = resolution?.ResolvedCode ?? rawCode;
            MarketType? marketType = resolution?.MarketType ?? (marketTypesByStockCode.TryGetValue(code, out var mt) ? mt : null);
            var sourceProvider = marketType is null ? null : ToSourceProviderText(marketType.Value);
            byCode.TryGetValue(code, out var maForOutput);

            if (holding.CurrentPrice is not decimal currentPrice)
            {
                // MA5／MA20／MA60／MA120 是依 TWSE／TPEx 官方收盤價自行計算，與 Excel「現價」欄位（DDE）無關；
                // 現價異常只代表無法判斷是否觸發，均價本身若已算出仍必須列在「每日五日均價策略」頁籤，不得因此留白。
                var issue = string.IsNullOrWhiteSpace(holding.CurrentPriceIssue) ? "原因不明" : holding.CurrentPriceIssue;
                result.Add(new StrategyAlert(
                    tradeDate,
                    AlertKind.CurrentPriceInvalid,
                    holding.WorkbookPath,
                    holding.SheetName,
                    holding.CustomerName,
                    holding.ExcelRow,
                    holding.StockCode,
                    holding.StockName,
                    null,
                    holding.Quantity,
                    maForOutput?.ClosePrice,
                    maForOutput?.MovingAverage5,
                    maForOutput?.MovingAverage20,
                    maForOutput?.MovingAverage60,
                    maForOutput?.MovingAverage120,
                    false, false, false,
                    $"現價無效，無法判斷：{issue}。請確認看盤軟體已開啟且 DDE 連線正常後，再重新執行。",
                    marketType,
                    null,
                    null,
                    sourceProvider,
                    calculatedAt,
                    "Excel現價異常",
                    issue,
                    maForOutput?.AvailableTradingDayCount ?? 0,
                    maForOutput?.LatestAvailableTradeDate));
                continue;
            }

            if (!byCode.TryGetValue(code, out var ma) || ma.ClosePrice is null)
            {
                var (missingReason, missingDiagnosticStatus) = ma is null
                    ? (marketType switch
                        {
                            MarketType.Emerging => "TPEx 興櫃官方當日行情尚無此股票資料，無法判斷均線，禁止使用昨日資料補值。",
                            _ => "TWSE／TPEx／TPEx興櫃 官方每日收盤價尚無此股票當日資料，無法判斷均線，禁止使用昨日資料補值。"
                        }, "當日收盤價缺失")
                    : (ma.MissingReason ?? "當日尚無官方收盤價資料，無法判斷均線，禁止使用昨日資料補值。", "當日收盤價缺失");
                result.Add(new StrategyAlert(
                    tradeDate,
                    AlertKind.TechnicalIndicatorMissing,
                    holding.WorkbookPath,
                    holding.SheetName,
                    holding.CustomerName,
                    holding.ExcelRow,
                    holding.StockCode,
                    holding.StockName,
                    currentPrice,
                    holding.Quantity,
                    null, null, null, null, null,
                    false, false, false,
                    missingReason,
                    marketType,
                    null,
                    null,
                    sourceProvider,
                    calculatedAt,
                    missingDiagnosticStatus,
                    missingReason,
                    ma?.AvailableTradingDayCount ?? 0,
                    ma?.LatestAvailableTradeDate));
                continue;
            }

            var ma5Triggered = ma.MovingAverage5 is decimal ma5 && ma5 <= currentPrice;
            var ma20Triggered = ma.MovingAverage20 is decimal ma20 && ma20 <= currentPrice;
            var ma120Triggered = ma.MovingAverage120 is decimal ma120 && ma120 <= currentPrice;

            if (!ma5Triggered && !ma20Triggered && !ma120Triggered)
            {
                continue;
            }

            // 均線本身可能因逐檔歷史資料不足或回補失敗而部分為 null（例如只有 MA5、缺 MA20／60／120），
            // 但只要已算出的均線確實觸發條件，仍必須通知；同時如實標示計算狀態與缺少原因，
            // 不得把「部分均線空白」當成沒有說明的正常結果（見 docs/01_需求與規則.md）。
            var diagnosticStatus = ma.CalculationStatus switch
            {
                CalculationStatus.Ok => "正常",
                CalculationStatus.BackfillFailed => "歷史回補失敗",
                _ => "歷史資料不足"
            };

            var triggers = new List<string>();
            if (ma5Triggered) triggers.Add("5 日均價");
            if (ma20Triggered) triggers.Add("20 日均價");
            if (ma120Triggered) triggers.Add("120 日均價");

            result.Add(new StrategyAlert(
                tradeDate,
                AlertKind.MovingAverageTriggered,
                holding.WorkbookPath,
                holding.SheetName,
                holding.CustomerName,
                holding.ExcelRow,
                holding.StockCode,
                holding.StockName,
                currentPrice,
                holding.Quantity,
                ma.ClosePrice,
                ma.MovingAverage5,
                ma.MovingAverage20,
                ma.MovingAverage60,
                ma.MovingAverage120,
                ma5Triggered,
                ma20Triggered,
                ma120Triggered,
                $"現價已大於或等於：{string.Join("、", triggers)}",
                marketType,
                null,
                null,
                sourceProvider,
                calculatedAt,
                diagnosticStatus,
                ma.MissingReason,
                ma.AvailableTradingDayCount,
                ma.LatestAvailableTradeDate));
        }

        return result;
    }

    private static string? ToSourceProviderText(MarketType marketType) => marketType switch
    {
        MarketType.Listed => "TWSE",
        MarketType.Otc => "TPEx",
        MarketType.Emerging => "TPEx興櫃",
        _ => null
    };

    /// <summary>統一委派給 <see cref="StockCodeNormalizer"/>，全系統唯一的股票代碼正規化規則來源。</summary>
    public static string NormalizeStockCode(string value) => StockCodeNormalizer.Normalize(value);
}
