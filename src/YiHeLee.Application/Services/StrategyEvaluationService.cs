using YiHeLee.Domain;

namespace YiHeLee.Application.Services;

/// <summary>
/// 均線策略判斷。正式 MA5／MA20／MA120 一律來自本系統依 TWSE／TPEx 官方收盤價自行計算的
/// <see cref="MovingAverageResult"/>；鉅亨網多頭／空頭排列僅作交叉驗證與清單保存，不再是正式均價來源。
/// </summary>
public sealed class StrategyEvaluationService
{
    /// <summary>
    /// 依需求逐項判斷 MA5、MA20、MA120 是否小於或等於進場價／平均價；任一條件成立即產生通知。
    /// MA60 僅保存與顯示，不參與觸發。任一均線因交易日數不足而為 null 時，該項不得觸發、也不得硬算。
    /// </summary>
    public IReadOnlyList<StrategyAlert> Evaluate(
        DateOnly tradeDate,
        IReadOnlyList<CustomerHolding> holdings,
        IReadOnlyList<MovingAverageResult> movingAverages,
        IReadOnlyDictionary<string, MarketType> marketTypesByStockCode,
        DateTimeOffset calculatedAt)
    {
        var byCode = movingAverages
            .GroupBy(x => NormalizeStockCode(x.StockCode), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.OrdinalIgnoreCase);

        var result = new List<StrategyAlert>(holdings.Count);

        foreach (var holding in holdings)
        {
            var code = NormalizeStockCode(holding.StockCode);
            MarketType? marketType = marketTypesByStockCode.TryGetValue(code, out var mt) ? mt : null;
            var sourceProvider = marketType is null ? null : ToSourceProviderText(marketType.Value);

            if (!byCode.TryGetValue(code, out var ma) || ma.ClosePrice is null)
            {
                var missingReason = marketType switch
                {
                    MarketType.Emerging => "TPEx 興櫃官方當日行情尚無此股票資料，無法判斷均線，禁止使用昨日資料補值。",
                    _ => "TWSE／TPEx／TPEx興櫃 官方每日收盤價尚無此股票當日資料，無法判斷均線，禁止使用昨日資料補值。"
                };
                result.Add(new StrategyAlert(
                    tradeDate,
                    AlertKind.TechnicalIndicatorMissing,
                    holding.WorkbookPath,
                    holding.SheetName,
                    holding.CustomerName,
                    holding.ExcelRow,
                    holding.StockCode,
                    holding.StockName,
                    holding.EntryAveragePrice,
                    holding.Quantity,
                    null, null, null, null, null,
                    false, false, false,
                    missingReason,
                    marketType,
                    null,
                    null,
                    sourceProvider,
                    calculatedAt));
                continue;
            }

            var ma5Triggered = ma.MovingAverage5 is decimal ma5 && ma5 <= holding.EntryAveragePrice;
            var ma20Triggered = ma.MovingAverage20 is decimal ma20 && ma20 <= holding.EntryAveragePrice;
            var ma120Triggered = ma.MovingAverage120 is decimal ma120 && ma120 <= holding.EntryAveragePrice;

            if (!ma5Triggered && !ma20Triggered && !ma120Triggered)
            {
                continue;
            }

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
                holding.EntryAveragePrice,
                holding.Quantity,
                ma.ClosePrice,
                ma.MovingAverage5,
                ma.MovingAverage20,
                ma.MovingAverage60,
                ma.MovingAverage120,
                ma5Triggered,
                ma20Triggered,
                ma120Triggered,
                $"進場價／平均價已大於或等於：{string.Join("、", triggers)}",
                marketType,
                null,
                null,
                sourceProvider,
                calculatedAt));
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

    public static string NormalizeStockCode(string value) => value.Trim().ToUpperInvariant();
}
