using YiHeLee.Domain;

namespace YiHeLee.Application.Services;

public sealed class StrategyEvaluationService
{
    /// <summary>
    /// 依需求逐項判斷 MA5、MA20、MA120 是否小於或等於進場價／平均價；
    /// 任一條件成立即產生通知。MA60 僅保存與顯示，不參與觸發。
    /// </summary>
    public IReadOnlyList<StrategyAlert> Evaluate(
        DateOnly tradeDate,
        IReadOnlyList<CustomerHolding> holdings,
        IReadOnlyList<TechnicalIndicator> indicators)
    {
        var byCode = indicators
            .GroupBy(x => NormalizeStockCode(x.StockCode), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(
                group => group.Key,
                group => group.OrderBy(x => x.IndicatorType).First(),
                StringComparer.OrdinalIgnoreCase);

        var result = new List<StrategyAlert>(holdings.Count);

        foreach (var holding in holdings)
        {
            var code = NormalizeStockCode(holding.StockCode);
            if (!byCode.TryGetValue(code, out var item))
            {
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
                    "目前兩個鉅亨排列清單皆未取得此股票，無法判斷均線",
                    null, null, null));
                continue;
            }

            var ma5 = item.MovingAverage5 <= holding.EntryAveragePrice;
            var ma20 = item.MovingAverage20 <= holding.EntryAveragePrice;
            var ma120 = item.MovingAverage120 <= holding.EntryAveragePrice;
            if (!ma5 && !ma20 && !ma120)
            {
                continue;
            }

            var triggers = new List<string>();
            if (ma5) triggers.Add("5 日均價");
            if (ma20) triggers.Add("20 日均價");
            if (ma120) triggers.Add("120 日均價");

            result.Add(new StrategyAlert(
                tradeDate,
                AlertKind.MovingAverageTriggered,
                holding.WorkbookPath,
                holding.SheetName,
                holding.CustomerName,
                holding.ExcelRow,
                holding.StockCode,
                string.IsNullOrWhiteSpace(holding.StockName) ? item.StockName : holding.StockName,
                holding.EntryAveragePrice,
                holding.Quantity,
                item.ClosePrice,
                item.MovingAverage5,
                item.MovingAverage20,
                item.MovingAverage60,
                item.MovingAverage120,
                ma5,
                ma20,
                ma120,
                $"進場價／平均價已大於或等於：{string.Join("、", triggers)}",
                item.MarketType,
                item.IndicatorType,
                item.SourceUrl));
        }

        return result;
    }

    public static string NormalizeStockCode(string value) => value.Trim().ToUpperInvariant();
}
