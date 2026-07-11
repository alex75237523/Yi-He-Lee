using YiHeLee.Domain;

namespace YiHeLee.Application.Services;

/// <summary>
/// 均線策略判斷。正式 MA5／MA20／MA120 一律來自本系統依 TWSE／TPEx 官方收盤價自行計算的
/// <see cref="MovingAverageResult"/>；鉅亨網多頭／空頭排列僅作交叉驗證與清單保存，不再是正式均價來源。
/// 2026-07-11 正式恢復雙價格判斷：客戶 Excel「進場價/平均價」與「現價」是兩個完全獨立、不得混用的欄位，
/// 每一條均價都必須「進場價/平均價」與「現價」同時大於或等於該均價才算成立（見 <see cref="IsDualPriceMatch"/>）。
/// </summary>
public sealed class StrategyEvaluationService
{
    /// <summary>
    /// 依需求逐項判斷 MA5、MA20、MA120：每一條均價都必須「進場價/平均價」與「現價」同時大於或等於該均價
    /// 才算成立（<see cref="IsDualPriceMatch"/>），任一條件成立即產生通知。MA60 僅保存與顯示，不參與觸發。
    /// 任一均線因交易日數不足而為 null 時，該項不得觸發、也不得硬算。
    /// 「進場價/平均價」與「現價」皆可能個別無效（Excel 錯誤值、空白、0、負數或無法解析）；兩者是完全獨立的
    /// 欄位，不得混用、不得互相代替、也不得只取其中一個判斷。任一價格無效就不得觸發，且必須分別產生對應的
    /// 異常通知（<see cref="AlertKind.EntryAveragePriceInvalid"/>／<see cref="AlertKind.CurrentPriceInvalid"/>），
    /// 兩者同時無效時兩個異常都必須讓使用者看見，不得只顯示其中一個。
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

            // 逐檔股票身分驗證優先於價格／均線判斷：代碼無法識別（例如 8 位數金額誤判、格式不符）
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
                    null,
                    holding.EntryAveragePrice,
                    null,
                    null));
                continue;
            }

            var code = resolution?.ResolvedCode ?? rawCode;
            MarketType? marketType = resolution?.MarketType ?? (marketTypesByStockCode.TryGetValue(code, out var mt) ? mt : null);
            var sourceProvider = marketType is null ? null : ToSourceProviderText(marketType.Value);
            byCode.TryGetValue(code, out var maForOutput);

            // 「進場價/平均價」與「現價」是兩個完全獨立的欄位，各自可能無效；任一無效都不得觸發，
            // 且必須各自產生對應的異常通知，不得只顯示其中一個而隱藏另一個。
            var entryAveragePriceInvalid = holding.EntryAveragePrice is null;
            var currentPriceInvalid = holding.CurrentPrice is null;

            if (entryAveragePriceInvalid || currentPriceInvalid)
            {
                if (entryAveragePriceInvalid)
                {
                    var issue = string.IsNullOrWhiteSpace(holding.EntryAveragePriceIssue) ? "原因不明" : holding.EntryAveragePriceIssue;
                    result.Add(new StrategyAlert(
                        tradeDate,
                        AlertKind.EntryAveragePriceInvalid,
                        holding.WorkbookPath,
                        holding.SheetName,
                        holding.CustomerName,
                        holding.ExcelRow,
                        holding.StockCode,
                        holding.StockName,
                        holding.CurrentPrice,
                        holding.Quantity,
                        maForOutput?.ClosePrice,
                        maForOutput?.MovingAverage5,
                        maForOutput?.MovingAverage20,
                        maForOutput?.MovingAverage60,
                        maForOutput?.MovingAverage120,
                        false, false, false,
                        $"進場價/平均價無效，無法判斷：{issue}。請確認 Excel 該欄位內容後再重新執行。",
                        marketType,
                        null,
                        null,
                        sourceProvider,
                        calculatedAt,
                        "進場價/平均價異常",
                        issue,
                        maForOutput?.AvailableTradingDayCount ?? 0,
                        maForOutput?.LatestAvailableTradeDate,
                        null,
                        issue,
                        holding.CurrentPriceIssue));
                }

                if (currentPriceInvalid)
                {
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
                        maForOutput?.LatestAvailableTradeDate,
                        holding.EntryAveragePrice,
                        holding.EntryAveragePriceIssue,
                        issue));
                }

                continue;
            }

            var entryAveragePrice = holding.EntryAveragePrice!.Value;
            var currentPrice = holding.CurrentPrice!.Value;

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
                    ma?.LatestAvailableTradeDate,
                    entryAveragePrice,
                    null,
                    null));
                continue;
            }

            var ma5Triggered = IsDualPriceMatch(ma.MovingAverage5, entryAveragePrice, currentPrice);
            var ma20Triggered = IsDualPriceMatch(ma.MovingAverage20, entryAveragePrice, currentPrice);
            var ma120Triggered = IsDualPriceMatch(ma.MovingAverage120, entryAveragePrice, currentPrice);

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
                $"進場價/平均價與現價已同時大於或等於：{string.Join("、", triggers)}",
                marketType,
                null,
                null,
                sourceProvider,
                calculatedAt,
                diagnosticStatus,
                ma.MissingReason,
                ma.AvailableTradingDayCount,
                ma.LatestAvailableTradeDate,
                entryAveragePrice,
                null,
                null));
        }

        return result;
    }

    /// <summary>
    /// 產生「每一筆有效持股」的完整計算結果，供 Excel「每日五日均價策略」頁籤輸出使用。
    /// 與 <see cref="Evaluate"/> 不同，本方法絕對不得因為未觸發、進場價/平均價或現價無效、或均線暫時缺口
    /// 而略過任何一筆持股；任一價格無效只能讓該筆的 <see cref="HoldingStrategyResult.OverallResult"/>
    /// 顯示為「暫時無法判斷」，其餘官方收盤價、MA5／MA20／MA60／MA120、有效交易日數、
    /// 鉅亨交叉驗證狀態一律照實填入，不得留白、不得標記為「均線計算失敗」。
    /// </summary>
    public IReadOnlyList<HoldingStrategyResult> EvaluateAll(
        DateOnly tradeDate,
        IReadOnlyList<CustomerHolding> holdings,
        IReadOnlyList<MovingAverageResult> movingAverages,
        IReadOnlyDictionary<string, MarketType> marketTypesByStockCode,
        DateTimeOffset calculatedAt,
        IReadOnlyDictionary<string, StockCodeResolution>? resolutions = null,
        IReadOnlyDictionary<string, string>? cnyesValidationStatuses = null)
    {
        var byCode = movingAverages
            .GroupBy(x => NormalizeStockCode(x.StockCode), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.OrdinalIgnoreCase);

        var result = new List<HoldingStrategyResult>(holdings.Count);

        foreach (var holding in holdings)
        {
            var rawCode = NormalizeStockCode(holding.StockCode);
            var resolution = resolutions is not null && resolutions.TryGetValue(rawCode, out var r) ? r : null;
            var currentPriceStatus = holding.CurrentPrice is not null ? "正常" : "無效";
            var entryAveragePriceStatus = holding.EntryAveragePrice is not null ? "正常" : "無效";

            // 逐檔股票身分驗證優先於價格／均線判斷：代碼無法識別或明確不納入均線策略（例如權證）時，
            // 直接產生診斷列；此類持股本來就不應查詢均線，但仍必須輸出一列讓使用者可追查原因。
            if (resolution is not null && (!resolution.IsRecognized || !resolution.Identity.IsEligibleForMovingAverageStrategy))
            {
                var identityDiagnosticStatus = !resolution.IsRecognized ? "股票代碼無法識別" : "非策略商品";
                var reasonText = resolution.UnrecognizedReason ?? resolution.Identity.IneligibleReason ?? "股票代碼無法識別或非策略商品。";
                result.Add(new HoldingStrategyResult(
                    tradeDate, holding.CustomerName, holding.SheetName, holding.ExcelRow,
                    holding.StockCode, resolution.ResolvedCode, holding.StockName,
                    resolution.MarketType,
                    holding.EntryAveragePrice, entryAveragePriceStatus, holding.EntryAveragePriceIssue,
                    holding.CurrentPrice, currentPriceStatus, holding.CurrentPriceIssue,
                    null, null, null, null, null, 0, null,
                    identityDiagnosticStatus, reasonText, "不適用",
                    false, false, false,
                    "無法判斷", reasonText, calculatedAt));
                continue;
            }

            var code = resolution?.ResolvedCode ?? rawCode;
            MarketType? marketType = resolution?.MarketType ?? (marketTypesByStockCode.TryGetValue(code, out var mt) ? mt : null);
            byCode.TryGetValue(code, out var ma);
            var cnyesStatus = cnyesValidationStatuses is not null && cnyesValidationStatuses.TryGetValue(code, out var cv) ? cv : "不適用";

            // 「進場價/平均價」與「現價」皆可能個別無效：僅影響最後的雙價格比較，均線本身若已算出
            // 仍必須照實輸出，不得留白，也不得標記為「均線計算失敗」。兩者同時無效時，兩個原因都必須
            // 能讓使用者看見，本型別每一筆持股只有一列，因此原因會合併顯示於 MissingReason／TriggerDescription。
            if (holding.EntryAveragePrice is null || holding.CurrentPrice is null)
            {
                var entryIssue = holding.EntryAveragePrice is null
                    ? (string.IsNullOrWhiteSpace(holding.EntryAveragePriceIssue) ? "原因不明" : holding.EntryAveragePriceIssue)
                    : null;
                var currentIssue = holding.CurrentPrice is null
                    ? (string.IsNullOrWhiteSpace(holding.CurrentPriceIssue) ? "原因不明" : holding.CurrentPriceIssue)
                    : null;
                var maCalcStatus = ma is null ? "當日收盤價缺失" : DescribeCalculationStatus(ma.CalculationStatus);

                var (overallResult, combinedReason) = (entryIssue, currentIssue) switch
                {
                    (not null, not null) => (
                        "現價與進場價/平均價皆無效，暫時無法判斷",
                        $"進場價/平均價無效：{entryIssue}；現價無效：{currentIssue}。請分別確認 Excel 儲存格內容與看盤軟體 DDE 連線後，再重新執行。"),
                    (not null, null) => (
                        "進場價/平均價無效，暫時無法判斷",
                        $"進場價/平均價無效，無法判斷：{entryIssue}。請確認 Excel 該欄位內容後再重新執行。"),
                    _ => (
                        "現價無效，暫時無法判斷",
                        $"現價無效，無法判斷：{currentIssue}。請確認看盤軟體已開啟且 DDE 連線正常後，再重新執行。")
                };

                result.Add(new HoldingStrategyResult(
                    tradeDate, holding.CustomerName, holding.SheetName, holding.ExcelRow,
                    holding.StockCode, code, holding.StockName,
                    marketType,
                    holding.EntryAveragePrice, entryAveragePriceStatus, holding.EntryAveragePriceIssue,
                    holding.CurrentPrice, currentPriceStatus, holding.CurrentPriceIssue,
                    ma?.ClosePrice, ma?.MovingAverage5, ma?.MovingAverage20, ma?.MovingAverage60, ma?.MovingAverage120,
                    ma?.AvailableTradingDayCount ?? 0, ma?.LatestAvailableTradeDate,
                    maCalcStatus, ma?.MissingReason, cnyesStatus,
                    false, false, false,
                    overallResult,
                    combinedReason,
                    calculatedAt));
                continue;
            }

            var entryAveragePrice = holding.EntryAveragePrice.Value;
            var currentPrice = holding.CurrentPrice.Value;

            if (ma is null || ma.ClosePrice is null)
            {
                var (missingReason, missingDiagnosticStatus) = ma is null
                    ? (marketType switch
                        {
                            MarketType.Emerging => "TPEx 興櫃官方當日行情尚無此股票資料，無法判斷均線，禁止使用昨日資料補值。",
                            _ => "TWSE／TPEx／TPEx興櫃 官方每日收盤價尚無此股票當日資料，無法判斷均線，禁止使用昨日資料補值。"
                        }, "當日收盤價缺失")
                    : (ma.MissingReason ?? "當日尚無官方收盤價資料，無法判斷均線，禁止使用昨日資料補值。", "當日收盤價缺失");
                result.Add(new HoldingStrategyResult(
                    tradeDate, holding.CustomerName, holding.SheetName, holding.ExcelRow,
                    holding.StockCode, code, holding.StockName,
                    marketType,
                    entryAveragePrice, entryAveragePriceStatus, holding.EntryAveragePriceIssue,
                    currentPrice, currentPriceStatus, holding.CurrentPriceIssue,
                    null, null, null, null, null,
                    ma?.AvailableTradingDayCount ?? 0, ma?.LatestAvailableTradeDate,
                    missingDiagnosticStatus, missingReason, cnyesStatus,
                    false, false, false,
                    "無法判斷", missingReason, calculatedAt));
                continue;
            }

            var ma5Triggered = IsDualPriceMatch(ma.MovingAverage5, entryAveragePrice, currentPrice);
            var ma20Triggered = IsDualPriceMatch(ma.MovingAverage20, entryAveragePrice, currentPrice);
            var ma120Triggered = IsDualPriceMatch(ma.MovingAverage120, entryAveragePrice, currentPrice);
            var anyTriggered = ma5Triggered || ma20Triggered || ma120Triggered;

            var triggers = new List<string>();
            if (ma5Triggered) triggers.Add("5 日均價");
            if (ma20Triggered) triggers.Add("20 日均價");
            if (ma120Triggered) triggers.Add("120 日均價");
            var triggerDescription = anyTriggered
                ? $"進場價/平均價與現價已同時大於或等於：{string.Join("、", triggers)}"
                : "進場價/平均價與現價未同時達到 MA5／MA20／MA120 任一條件，未觸發。";

            result.Add(new HoldingStrategyResult(
                tradeDate, holding.CustomerName, holding.SheetName, holding.ExcelRow,
                holding.StockCode, code, holding.StockName,
                marketType,
                entryAveragePrice, entryAveragePriceStatus, holding.EntryAveragePriceIssue,
                currentPrice, currentPriceStatus, holding.CurrentPriceIssue,
                ma.ClosePrice, ma.MovingAverage5, ma.MovingAverage20, ma.MovingAverage60, ma.MovingAverage120,
                ma.AvailableTradingDayCount, ma.LatestAvailableTradeDate,
                DescribeCalculationStatus(ma.CalculationStatus), ma.MissingReason, cnyesStatus,
                ma5Triggered, ma20Triggered, ma120Triggered,
                anyTriggered ? "觸發" : "未觸發",
                triggerDescription,
                calculatedAt));
        }

        return result;
    }

    /// <summary>
    /// 單一均價的雙價格判斷核心：「進場價/平均價」與「現價」必須同時大於或等於該均價才算成立。
    /// <see cref="Evaluate"/>、<see cref="EvaluateAll"/> 與畫面顯示都必須透過本方法判斷，
    /// 不得各自重複實作而產生不一致的結果。均線為 null（交易日數不足）時一律不成立，也不得硬算。
    /// </summary>
    private static bool IsDualPriceMatch(
        decimal? movingAverage,
        decimal entryAveragePrice,
        decimal currentPrice)
    {
        return movingAverage is decimal ma
            && entryAveragePrice >= ma
            && currentPrice >= ma;
    }

    private static string DescribeCalculationStatus(CalculationStatus status) => status switch
    {
        CalculationStatus.Ok => "正常",
        CalculationStatus.BackfillFailed => "歷史回補失敗",
        CalculationStatus.TodayCloseMissing => "當日收盤價缺失",
        _ => "歷史資料不足"
    };

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
