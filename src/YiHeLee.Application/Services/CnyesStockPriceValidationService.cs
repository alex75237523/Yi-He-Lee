using YiHeLee.Application.Abstractions;
using YiHeLee.Domain;

namespace YiHeLee.Application.Services;

/// <summary>
/// 以鉅亨網多頭／空頭排列清單交叉驗證本系統依官方收盤價自算的 MA5／MA20／MA60／MA120。
/// 比對規則：
/// 1. 鉅亨頁面日期必須等於目標交易日期，否則整批標記為 SourceDateMismatch，拒絕比對。
/// 2. 股票未出現在當日多頭／空頭清單者，標記為 NotApplicable，不代表計算錯誤。
/// 3. 本系統均線因有效交易日數不足為 null 時，標記為 InsufficientHistory，不得以較少天數硬算比對。
/// 4. 其餘情形比對絕對差異，&lt;=0.01 為 Matched，否則 Mismatched。
/// 全程不寫入或覆蓋 StockDailyPrice／StockMovingAverage，鉅亨網失敗也不影響官方資料。
/// </summary>
public sealed class CnyesStockPriceValidationService : IStockPriceValidationService
{
    private static readonly int[] Windows = [5, 20, 60, 120];
    private const decimal ToleranceThreshold = 0.01m;

    private readonly IMovingAverageService _movingAverageService;
    private readonly IStockPriceValidationRepository _validationRepository;
    private readonly IClock _clock;
    private readonly IAppLogger _logger;

    public CnyesStockPriceValidationService(
        IMovingAverageService movingAverageService,
        IStockPriceValidationRepository validationRepository,
        IClock clock,
        IAppLogger logger)
    {
        _movingAverageService = movingAverageService;
        _validationRepository = validationRepository;
        _clock = clock;
        _logger = logger;
    }

    public async Task<IReadOnlyList<CnyesValidationRecord>> ValidateAsync(
        DateOnly tradeDate,
        IReadOnlyDictionary<string, MarketType> stockMarketTypes,
        IReadOnlyList<CrawlBatch> cnyesBatches,
        CancellationToken cancellationToken)
    {
        var now = _clock.GetTaipeiNow();
        var records = new List<CnyesValidationRecord>();

        var matchingBatches = cnyesBatches.Where(b => b.PageDate == tradeDate).ToList();
        var mismatchedBatches = cnyesBatches.Where(b => b.PageDate != tradeDate).ToList();

        foreach (var batch in mismatchedBatches)
        {
            var message = $"鉅亨網頁面日期為 {batch.PageDate:yyyy-MM-dd}，與目標交易日期 {tradeDate:yyyy-MM-dd} 不符，拒絕比對。";
            foreach (var item in batch.Items)
            {
                records.Add(new CnyesValidationRecord(
                    tradeDate, batch.MarketType, item.StockCode, 0, null, null, null,
                    CnyesValidationOutcome.SourceDateMismatch, batch.PageDate, batch.Source.Url.ToString(), now, message));
            }
        }

        var cnyesByCode = matchingBatches
            .SelectMany(batch => batch.Items.Select(item => (Batch: batch, Item: item)))
            .GroupBy(x => x.Item.StockCode, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);

        // 已因鉅亨頁面日期不符而記錄 SourceDateMismatch 的股票，不得再因「未出現在清單」重複產生矛盾紀錄。
        var mismatchedCodes = mismatchedBatches
            .SelectMany(batch => batch.Items.Select(item => item.StockCode))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        if (stockMarketTypes.Count > 0)
        {
            var maResults = await _movingAverageService
                .CalculateManyAsync(stockMarketTypes.Keys.ToArray(), tradeDate, cancellationToken)
                .ConfigureAwait(false);
            var maByCode = maResults.ToDictionary(x => x.StockCode, StringComparer.OrdinalIgnoreCase);

            foreach (var (code, marketType) in stockMarketTypes)
            {
                if (mismatchedCodes.Contains(code))
                {
                    continue;
                }

                if (!cnyesByCode.TryGetValue(code, out var cnyes))
                {
                    records.Add(matchingBatches.Count > 0
                        ? new CnyesValidationRecord(tradeDate, marketType, code, 0, null, null, null,
                            CnyesValidationOutcome.NotApplicable, tradeDate, null, now, null)
                        : new CnyesValidationRecord(tradeDate, marketType, code, 0, null, null, null,
                            CnyesValidationOutcome.SourceUnavailable, null, null, now,
                            "本次未取得目標交易日期之鉅亨網多頭／空頭排列清單，無法交叉驗證，不影響官方資料。"));
                    continue;
                }

                maByCode.TryGetValue(code, out var ma);
                foreach (var window in Windows)
                {
                    var ours = window switch
                    {
                        5 => ma?.MovingAverage5,
                        20 => ma?.MovingAverage20,
                        60 => ma?.MovingAverage60,
                        120 => ma?.MovingAverage120,
                        _ => null
                    };
                    var cnyesValue = window switch
                    {
                        5 => cnyes.Item.MovingAverage5,
                        20 => cnyes.Item.MovingAverage20,
                        60 => cnyes.Item.MovingAverage60,
                        120 => cnyes.Item.MovingAverage120,
                        _ => 0m
                    };

                    if (ours is null)
                    {
                        records.Add(new CnyesValidationRecord(
                            tradeDate, marketType, code, window, null, cnyesValue, null,
                            CnyesValidationOutcome.InsufficientHistory, cnyes.Batch.PageDate, cnyes.Batch.Source.Url.ToString(), now,
                            "本系統目前累積之有效交易日數不足，不得以較少天數硬算比對。"));
                        continue;
                    }

                    var diff = Math.Abs(ours.Value - cnyesValue);
                    var outcome = diff <= ToleranceThreshold ? CnyesValidationOutcome.Matched : CnyesValidationOutcome.Mismatched;
                    records.Add(new CnyesValidationRecord(
                        tradeDate, marketType, code, window, ours, cnyesValue, diff,
                        outcome, cnyes.Batch.PageDate, cnyes.Batch.Source.Url.ToString(), now, null));

                    if (outcome == CnyesValidationOutcome.Mismatched)
                    {
                        _logger.Warning(
                            $"鉅亨交叉驗證差異：{code}／MA{window}，自算={ours:0.0000}，鉅亨={cnyesValue:0.0000}，差異={diff:0.0000}。");
                    }
                }
            }
        }

        if (records.Count > 0)
        {
            await _validationRepository.SaveValidationRecordsAsync(records, cancellationToken).ConfigureAwait(false);
        }

        return records;
    }
}
