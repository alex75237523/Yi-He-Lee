using YiHeLee.Application.Abstractions;
using YiHeLee.Domain;

namespace YiHeLee.Application.Services;

/// <summary>
/// 官方每日收盤價協調服務。負責：
/// 台北日期與來源資料日期比對、休市／尚未公布判斷、冪等 Upsert、批次狀態記錄。
/// TWSE 對非交易日會明確回報「查無資料」，做為休市權威判斷依據；
/// TPEx 對非交易日會靜默回傳最近一個交易日資料，因此只有在同一天 TWSE 已確認休市時，
/// 才會把 TPEx 的日期不符情形也記錄為 Holiday，否則一律視為 NotPublished 並等待重試，
/// 絕不允許把前一交易日資料當成目標日期寫入正式資料表。
/// </summary>
public sealed class MarketPriceService : IMarketPriceService
{
    private readonly ITwseMarketDataProvider _twseProvider;
    private readonly ITpexMarketDataProvider _tpexProvider;
    private readonly IEmergingMarketDataProvider _emergingProvider;
    private readonly IMarketDataRepository _repository;
    private readonly IClock _clock;
    private readonly IAppLogger _logger;

    public MarketPriceService(
        ITwseMarketDataProvider twseProvider,
        ITpexMarketDataProvider tpexProvider,
        IEmergingMarketDataProvider emergingProvider,
        IMarketDataRepository repository,
        IClock clock,
        IAppLogger logger)
    {
        _twseProvider = twseProvider;
        _tpexProvider = tpexProvider;
        _emergingProvider = emergingProvider;
        _repository = repository;
        _clock = clock;
        _logger = logger;
    }

    public async Task<IReadOnlyList<OfficialPriceBatchSummary>> FetchAndSaveDailyPricesAsync(
        DateOnly targetDate,
        OfficialMarketDataSettings settings,
        CancellationToken cancellationToken)
    {
        var summaries = new List<OfficialPriceBatchSummary>();

        if (targetDate.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday)
        {
            // 週末非交易日，無需呼叫官方來源即可確定為休市，避免不必要的網路請求。
            summaries.Add(await RecordHolidayWithoutFetchAsync(OfficialPriceJobType.DailyMarketData, targetDate, _twseProvider.SourceProviderName, MarketType.Listed, cancellationToken).ConfigureAwait(false));
            summaries.Add(await RecordHolidayWithoutFetchAsync(OfficialPriceJobType.DailyMarketData, targetDate, _tpexProvider.SourceProviderName, MarketType.Otc, cancellationToken).ConfigureAwait(false));
            return summaries;
        }

        var twseSummary = await FetchAndSaveOneAsync(OfficialPriceJobType.DailyMarketData, targetDate, MarketType.Listed, settings, cancellationToken).ConfigureAwait(false);
        summaries.Add(twseSummary);

        var twseIsHoliday = twseSummary.Status == OfficialPriceBatchStatus.Holiday;
        var tpexSummary = await FetchAndSaveOneAsync(
            OfficialPriceJobType.DailyMarketData, targetDate, MarketType.Otc, settings, cancellationToken,
            treatDateMismatchAsHoliday: twseIsHoliday).ConfigureAwait(false);
        summaries.Add(tpexSummary);

        return summaries;
    }

    public async Task<IReadOnlyList<OfficialPriceBatchSummary>> BackfillHistoryAsync(
        DateOnly targetDate,
        OfficialMarketDataSettings settings,
        CancellationToken cancellationToken,
        Action<string>? reportProgress = null,
        IReadOnlyCollection<string>? emergingStockCodes = null,
        IReadOnlyCollection<string>? listedStockCodes = null,
        IReadOnlyCollection<string>? otcStockCodes = null)
    {
        var summaries = new List<OfficialPriceBatchSummary>();
        var requiredTradingDays = Math.Max(1, settings.RequiredTradingDaysForMa120);
        var maxLookbackCalendarDays = Math.Max(requiredTradingDays, settings.MaxBackfillLookbackCalendarDays);
        var emergingCodes = NormalizeCodes(emergingStockCodes);
        var listedCodes = NormalizeCodes(listedStockCodes);
        var otcCodes = NormalizeCodes(otcStockCodes);

        // 歷史回補只補建 targetDate「以前」的資料；targetDate 當日一律由每日正式排程負責，兩者不得混用。
        // 判斷「是否已足夠」一律以固定基準日（targetDate 前一日）為準，不可隨 cursor 逐日倒退而跟著縮小查詢範圍。
        // 上市與上櫃必須分開計算，避免上市已滿 120 日時誤判上櫃也足夠，導致上櫃股 MA5／MA20 持續空白。
        var referenceDate = targetDate.AddDays(-1);
        var cursor = referenceDate;
        for (var walked = 0; walked < maxLookbackCalendarDays; walked++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var listedTradingDays = await _repository.GetDistinctTradeDateCountAsync(referenceDate, requiredTradingDays, MarketType.Listed, cancellationToken).ConfigureAwait(false);
            var otcTradingDays = await _repository.GetDistinctTradeDateCountAsync(referenceDate, requiredTradingDays, MarketType.Otc, cancellationToken).ConfigureAwait(false);

            // 完成判斷不得只看「市場整體」交易日數：即使整體市場已累積足夠天數，仍必須逐檔確認
            // 傳入的持股個別是否也達標（例如上櫃整體已有 120 日，但個股鈺創／群聯／碩禾只有 83／76／65 筆）。
            var listedStockCounts = await GetStockTradingDayCountsAsync(listedCodes, referenceDate, requiredTradingDays, cancellationToken).ConfigureAwait(false);
            var otcStockCounts = await GetStockTradingDayCountsAsync(otcCodes, referenceDate, requiredTradingDays, cancellationToken).ConfigureAwait(false);
            var emergingTradingDayCounts = await GetStockTradingDayCountsAsync(emergingCodes, referenceDate, requiredTradingDays, cancellationToken).ConfigureAwait(false);

            var listedInsufficientStocks = listedCodes.Where(code => listedStockCounts[code] < requiredTradingDays).ToArray();
            var otcInsufficientStocks = otcCodes.Where(code => otcStockCounts[code] < requiredTradingDays).ToArray();
            var insufficientEmergingCodes = emergingCodes.Where(code => emergingTradingDayCounts[code] < requiredTradingDays).ToArray();

            var listedSufficient = listedTradingDays >= requiredTradingDays && listedInsufficientStocks.Length == 0;
            var otcSufficient = otcTradingDays >= requiredTradingDays && otcInsufficientStocks.Length == 0;

            if (listedSufficient && otcSufficient && insufficientEmergingCodes.Length == 0)
            {
                var emergingText = emergingCodes.Length == 0
                    ? "無興櫃持股需回補"
                    : $"興櫃持股 {emergingCodes.Length} 檔皆已足夠";
                reportProgress?.Invoke($"MA120 歷史資料已足夠（上市 {listedTradingDays}/{requiredTradingDays}、上櫃 {otcTradingDays}/{requiredTradingDays} 個交易日，{emergingText}），不需回補。");
                break;
            }

            if (cursor.DayOfWeek is not (DayOfWeek.Saturday or DayOfWeek.Sunday))
            {
                OfficialPriceBatchSummary? twseSummary = null;
                OfficialPriceBatchSummary? tpexSummary = null;
                OfficialPriceBatchSummary? emergingSummary = null;

                if (!listedSufficient)
                {
                    reportProgress?.Invoke(
                        $"正在自動回補 MA120 歷史收盤價：抓取 {cursor:yyyy-MM-dd} 上市（TWSE）全部股票收盤價……" +
                        $"（上市已累積 {listedTradingDays}/{requiredTradingDays} 個交易日{DescribeInsufficientStocks(listedInsufficientStocks, listedStockCounts, requiredTradingDays)}）");
                    twseSummary = await FetchAndSaveOneAsync(OfficialPriceJobType.HistoricalBackfill, cursor, MarketType.Listed, settings, cancellationToken).ConfigureAwait(false);
                    summaries.Add(twseSummary);
                }
                else
                {
                    reportProgress?.Invoke($"上市（TWSE）歷史資料已足夠（{listedTradingDays}/{requiredTradingDays}），本日自動回補略過上市。");
                }

                if (!otcSufficient)
                {
                    var isHoliday = twseSummary?.Status == OfficialPriceBatchStatus.Holiday;
                    reportProgress?.Invoke(
                        $"正在自動回補 MA120 歷史收盤價：抓取 {cursor:yyyy-MM-dd} 上櫃（TPEx）全部股票收盤價……" +
                        $"（上櫃已累積 {otcTradingDays}/{requiredTradingDays} 個交易日{DescribeInsufficientStocks(otcInsufficientStocks, otcStockCounts, requiredTradingDays)}）");
                    tpexSummary = await FetchAndSaveOneAsync(
                        OfficialPriceJobType.HistoricalBackfill, cursor, MarketType.Otc, settings, cancellationToken,
                        treatDateMismatchAsHoliday: isHoliday).ConfigureAwait(false);
                    summaries.Add(tpexSummary);
                }
                else
                {
                    reportProgress?.Invoke($"上櫃（TPEx）歷史資料已足夠（{otcTradingDays}/{requiredTradingDays}），本日自動回補略過上櫃。");
                }

                if (insufficientEmergingCodes.Length > 0)
                {
                    var missingCodes = new List<string>(insufficientEmergingCodes.Length);
                    foreach (var code in insufficientEmergingCodes)
                    {
                        if (!await _repository.HasDailyPriceAsync(cursor, code, cancellationToken).ConfigureAwait(false))
                        {
                            missingCodes.Add(code);
                        }
                    }

                    if (missingCodes.Count > 0)
                    {
                        var minCount = insufficientEmergingCodes.Min(code => emergingTradingDayCounts[code]);
                        reportProgress?.Invoke(
                            $"正在自動回補 MA120 歷史收盤價：抓取 {cursor:yyyy-MM-dd} 興櫃（TPEx）持股 {missingCodes.Count} 檔（{string.Join("、", missingCodes.Take(8))}{(missingCodes.Count > 8 ? "…" : string.Empty)}）……" +
                            $"（興櫃最少已累積 {minCount}/{requiredTradingDays} 個交易日）");
                        emergingSummary = await FetchAndSaveEmergingHistoricalAsync(
                            cursor, missingCodes, settings, cancellationToken).ConfigureAwait(false);
                        summaries.Add(emergingSummary);
                    }
                    else
                    {
                        reportProgress?.Invoke($"興櫃（TPEx）{cursor:yyyy-MM-dd} 需要的持股資料 DB 已有，本日略過重複回補。");
                    }
                }

                reportProgress?.Invoke(DescribeBackfillDay(
                    cursor, twseSummary, tpexSummary, emergingSummary,
                    listedTradingDays, otcTradingDays, emergingTradingDayCounts,
                    requiredTradingDays));

                // 避免大量平行轟炸官方網站；逐日之間節流。
                await Task.Delay(TimeSpan.FromMilliseconds(Math.Max(0, settings.BackfillThrottleMillisecondsBetweenRequests)), cancellationToken).ConfigureAwait(false);
            }

            cursor = cursor.AddDays(-1);
        }

        return summaries;
    }

    /// <summary>正規化並去重股票代碼清單；null 時回傳空陣列，維持向下相容（不指定持股代碼時只檢查市場整體）。</summary>
    private static string[] NormalizeCodes(IReadOnlyCollection<string>? codes)
        => (codes ?? [])
            .Select(StrategyEvaluationService.NormalizeStockCode)
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

    /// <summary>組出「其中 X 檔仍不足」的細節文字，找不到值時視為 0（尚無任何資料）。</summary>
    private static string DescribeInsufficientStocks(
        IReadOnlyCollection<string> insufficientCodes,
        IReadOnlyDictionary<string, int> counts,
        int requiredTradingDays)
    {
        if (insufficientCodes.Count == 0)
        {
            return string.Empty;
        }

        var sample = insufficientCodes
            .Take(5)
            .Select(code => $"{code}:{(counts.TryGetValue(code, out var c) ? c : 0)}/{requiredTradingDays}")
            .ToArray();
        var more = insufficientCodes.Count > sample.Length ? "…" : string.Empty;
        return $"，其中持股 {insufficientCodes.Count} 檔逐檔檢查仍不足（{string.Join("、", sample)}{more}）";
    }

    /// <summary>組出單日回補結果的細節文字（供畫面顯示），例如「上市 新增 1023 筆、上櫃 新增 812 筆」或「休市」。</summary>
    private static string DescribeBackfillDay(
        DateOnly date,
        OfficialPriceBatchSummary? twse,
        OfficialPriceBatchSummary? tpex,
        OfficialPriceBatchSummary? emerging,
        int listedTradingDays,
        int otcTradingDays,
        IReadOnlyDictionary<string, int> emergingTradingDayCounts,
        int requiredTradingDays)
    {
        static string Describe(string marketName, OfficialPriceBatchSummary? summary) => summary?.Status switch
        {
            OfficialPriceBatchStatus.Succeeded => $"{marketName} 新增 {summary.InsertedCount} 筆、更新 {summary.UpdatedCount} 筆",
            OfficialPriceBatchStatus.Holiday => $"{marketName} 休市",
            OfficialPriceBatchStatus.NotPublished => $"{marketName} 尚未公布",
            null => $"{marketName} 已足夠略過",
            _ => $"{marketName} 失敗"
        };

        var emergingProgress = emergingTradingDayCounts.Count == 0
            ? "興櫃無持股"
            : $"興櫃持股最少 {emergingTradingDayCounts.Min(x => x.Value)}/{requiredTradingDays}";

        return $"已完成 {date:yyyy-MM-dd} 自動回補：{Describe("上市", twse)}、{Describe("上櫃", tpex)}、{Describe("興櫃", emerging)}" +
               $"（回補前累積：上市 {listedTradingDays}/{requiredTradingDays}、上櫃 {otcTradingDays}/{requiredTradingDays}、{emergingProgress} 個交易日）";
    }

    /// <summary>
    /// 逐檔取得指定股票在 referenceDate（含）以前的有效交易日數；用於逐檔歷史完整性判斷，
    /// 不得只用市場整體交易日數判斷單一股票是否已足夠回補。stockCodes 為空時回傳空字典。
    /// </summary>
    private async Task<IReadOnlyDictionary<string, int>> GetStockTradingDayCountsAsync(
        IReadOnlyCollection<string> stockCodes,
        DateOnly referenceDate,
        int requiredTradingDays,
        CancellationToken cancellationToken)
    {
        var result = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var code in stockCodes)
        {
            cancellationToken.ThrowIfCancellationRequested();
            result[code] = await _repository.GetDistinctTradeDateCountAsync(
                referenceDate, requiredTradingDays, code, cancellationToken).ConfigureAwait(false);
        }

        return result;
    }

    private async Task<OfficialPriceBatchSummary> FetchAndSaveEmergingHistoricalAsync(
        DateOnly targetDate,
        IReadOnlyCollection<string> stockCodes,
        OfficialMarketDataSettings settings,
        CancellationToken cancellationToken)
    {
        var providerName = ResolveProviderName(MarketType.Emerging);
        if (targetDate.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday)
        {
            return await RecordHolidayWithoutFetchAsync(
                OfficialPriceJobType.HistoricalBackfill, targetDate, providerName, MarketType.Emerging, cancellationToken).ConfigureAwait(false);
        }

        var startedAt = _clock.GetTaipeiNow();
        var batchId = await _repository.BeginPriceBatchAsync(
            OfficialPriceJobType.HistoricalBackfill, targetDate, providerName, MarketType.Emerging, startedAt, cancellationToken).ConfigureAwait(false);

        OfficialPriceFetchResult fetchResult;
        try
        {
            fetchResult = await _emergingProvider.FetchHistoricalDailyCloseAsync(targetDate, stockCodes, settings, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            var failSummary = new OfficialPriceBatchSummary(
                batchId, OfficialPriceJobType.HistoricalBackfill, targetDate, providerName, MarketType.Emerging, null,
                startedAt, _clock.GetTaipeiNow(), 0, 0, 0, 0, 0, 0,
                OfficialPriceBatchStatus.Failed, ex.Message);
            await _repository.CompletePriceBatchAsync(failSummary, cancellationToken).ConfigureAwait(false);
            _logger.Error($"{providerName} {targetDate:yyyy-MM-dd} 興櫃歷史行情擷取失敗。", ex);
            return failSummary;
        }

        if (fetchResult.IsHolidayOrNoData)
        {
            var holidaySummary = new OfficialPriceBatchSummary(
                batchId, OfficialPriceJobType.HistoricalBackfill, targetDate, providerName, MarketType.Emerging, fetchResult.SourceDataDate,
                startedAt, _clock.GetTaipeiNow(), fetchResult.Quotes.Count, 0, 0, 0, 0, 0,
                OfficialPriceBatchStatus.Holiday, "TPEx 興櫃個股歷史行情當日查無持股成交均價，未寫入資料。");
            await _repository.CompletePriceBatchAsync(holidaySummary, cancellationToken).ConfigureAwait(false);
            return holidaySummary;
        }

        if (fetchResult.SourceDataDate is null || fetchResult.SourceDataDate.Value != targetDate)
        {
            var mismatchSummary = new OfficialPriceBatchSummary(
                batchId, OfficialPriceJobType.HistoricalBackfill, targetDate, providerName, MarketType.Emerging, fetchResult.SourceDataDate,
                startedAt, _clock.GetTaipeiNow(), fetchResult.Quotes.Count, 0, 0, fetchResult.Quotes.Count, 0, 0,
                OfficialPriceBatchStatus.NotPublished,
                $"{providerName} 興櫃歷史行情來源日期為 {fetchResult.SourceDataDate?.ToString("yyyy-MM-dd") ?? "空白"}，不等於目標日期 {targetDate:yyyy-MM-dd}，拒絕寫入。");
            await _repository.CompletePriceBatchAsync(mismatchSummary, cancellationToken).ConfigureAwait(false);
            return mismatchSummary;
        }

        var prices = fetchResult.Quotes
            .Select(quote => new OfficialStockPrice(
                quote.StockCode,
                quote.StockName,
                MarketType.Emerging,
                targetDate,
                quote.ClosePrice,
                providerName,
                fetchResult.SourceUrl,
                targetDate,
                batchId,
                fetchResult.FetchCompletedAt))
            .ToArray();

        var (inserted, updated) = await _repository.UpsertDailyPricesAsync(prices, cancellationToken).ConfigureAwait(false);
        var successSummary = new OfficialPriceBatchSummary(
            batchId, OfficialPriceJobType.HistoricalBackfill, targetDate, providerName, MarketType.Emerging, targetDate,
            startedAt, _clock.GetTaipeiNow(), prices.Length, inserted, updated, 0, 0, 0,
            OfficialPriceBatchStatus.Succeeded, null);
        await _repository.CompletePriceBatchAsync(successSummary, cancellationToken).ConfigureAwait(false);
        return successSummary;
    }

    public async Task<OfficialPriceBatchSummary> FetchAndSaveSingleAsync(
        OfficialPriceJobType jobType,
        DateOnly targetDate,
        MarketType marketType,
        OfficialMarketDataSettings settings,
        CancellationToken cancellationToken)
        => await FetchAndSaveOneAsync(jobType, targetDate, marketType, settings, cancellationToken).ConfigureAwait(false);

    private async Task<OfficialPriceBatchSummary> FetchAndSaveOneAsync(
        OfficialPriceJobType jobType,
        DateOnly targetDate,
        MarketType marketType,
        OfficialMarketDataSettings settings,
        CancellationToken cancellationToken,
        bool treatDateMismatchAsHoliday = false)
    {
        var providerName = ResolveProviderName(marketType);

        if (targetDate.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday)
        {
            // 週末非交易日，防禦性檢查：即使呼叫端（例如歷史回補並行服務）未先行判斷，也不會對官方來源發送不必要的請求。
            return await RecordHolidayWithoutFetchAsync(jobType, targetDate, providerName, marketType, cancellationToken).ConfigureAwait(false);
        }

        if (jobType == OfficialPriceJobType.HistoricalBackfill
            && await _repository.HasDailyPricesAsync(targetDate, marketType, cancellationToken).ConfigureAwait(false))
        {
            _logger.Info($"{providerName} {targetDate:yyyy-MM-dd}（{jobType}）DB 已有正式收盤價資料，略過重複回補。");
            return CreateSyntheticSummary(jobType, targetDate, providerName, marketType, OfficialPriceBatchStatus.Succeeded, "DB 已保存此市場此交易日的正式收盤價，本次略過重複回補。");
        }

        if (await _repository.HasSucceededBatchAsync(jobType, targetDate, providerName, cancellationToken).ConfigureAwait(false))
        {
            _logger.Info($"{providerName} {targetDate:yyyy-MM-dd}（{jobType}）官方收盤價本次已成功保存過，略過重複抓取。");
            return CreateSyntheticSummary(jobType, targetDate, providerName, marketType, OfficialPriceBatchStatus.Succeeded, "同一目標日期先前已成功寫入，本次略過重複抓取（冪等）。");
        }

        var startedAt = _clock.GetTaipeiNow();
        var batchId = await _repository.BeginPriceBatchAsync(jobType, targetDate, providerName, marketType, startedAt, cancellationToken).ConfigureAwait(false);

        OfficialPriceFetchResult fetchResult;
        try
        {
            fetchResult = marketType switch
            {
                MarketType.Listed => await _twseProvider.FetchDailyCloseAsync(targetDate, settings, cancellationToken).ConfigureAwait(false),
                MarketType.Otc => await _tpexProvider.FetchDailyCloseAsync(targetDate, settings, cancellationToken).ConfigureAwait(false),
                MarketType.Emerging => await _emergingProvider.FetchDailyCloseAsync(targetDate, settings, cancellationToken).ConfigureAwait(false),
                _ => throw new ArgumentOutOfRangeException(nameof(marketType), marketType, "不支援的市場別。")
            };
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            var failSummary = new OfficialPriceBatchSummary(
                batchId, jobType, targetDate, providerName, marketType, null,
                startedAt, _clock.GetTaipeiNow(), 0, 0, 0, 0, 0, 0,
                OfficialPriceBatchStatus.Failed, ex.Message);
            await _repository.CompletePriceBatchAsync(failSummary, cancellationToken).ConfigureAwait(false);
            _logger.Error($"{providerName} {targetDate:yyyy-MM-dd} 官方每日收盤價擷取失敗。", ex);
            return failSummary;
        }

        if (fetchResult.IsHolidayOrNoData)
        {
            var holidaySummary = new OfficialPriceBatchSummary(
                batchId, jobType, targetDate, providerName, marketType, fetchResult.SourceDataDate,
                startedAt, _clock.GetTaipeiNow(), fetchResult.Quotes.Count, 0, 0, 0, 0, 0,
                OfficialPriceBatchStatus.Holiday, "官方來源明確回報查無資料（休市或非交易日）。");
            await _repository.CompletePriceBatchAsync(holidaySummary, cancellationToken).ConfigureAwait(false);
            _logger.Info($"{providerName} {targetDate:yyyy-MM-dd} 官方來源回報休市／無交易資料。");
            return holidaySummary;
        }

        // 核心規則：來源資料日期必須完全等於 targetDate，否則絕不可寫入正式資料，也不得回抓前一交易日。
        if (fetchResult.SourceDataDate is null || fetchResult.SourceDataDate.Value != targetDate)
        {
            var status = treatDateMismatchAsHoliday ? OfficialPriceBatchStatus.Holiday : OfficialPriceBatchStatus.NotPublished;
            var message = treatDateMismatchAsHoliday
                ? $"{providerName} 回傳資料日期為 {fetchResult.SourceDataDate:yyyy-MM-dd}，與同日 TWSE 已確認之休市狀態一致，記錄為休市，不寫入正式資料。"
                : $"{providerName} 回傳資料日期為「{fetchResult.SourceDataDate?.ToString("yyyy-MM-dd") ?? "無"}」，尚未等於目標日期 {targetDate:yyyy-MM-dd}，依規定不得寫入正式資料表，待重試機制稍後重試。";
            var mismatchSummary = new OfficialPriceBatchSummary(
                batchId, jobType, targetDate, providerName, marketType, fetchResult.SourceDataDate,
                startedAt, _clock.GetTaipeiNow(), fetchResult.Quotes.Count, 0, 0, fetchResult.Quotes.Count, 0, 0,
                status, message);
            await _repository.CompletePriceBatchAsync(mismatchSummary, cancellationToken).ConfigureAwait(false);
            _logger.Warning(message);
            return mismatchSummary;
        }

        if (fetchResult.Quotes.Count == 0)
        {
            var emptySummary = new OfficialPriceBatchSummary(
                batchId, jobType, targetDate, providerName, marketType, fetchResult.SourceDataDate,
                startedAt, _clock.GetTaipeiNow(), 0, 0, 0, 0, 0, 0,
                OfficialPriceBatchStatus.Failed, "來源資料日期正確，但沒有任何可解析的股票收盤價，視為解析失敗，不得標記為成功。");
            await _repository.CompletePriceBatchAsync(emptySummary, cancellationToken).ConfigureAwait(false);
            _logger.Warning(emptySummary.ErrorMessage!);
            return emptySummary;
        }

        var prices = fetchResult.Quotes
            .Select(quote => new OfficialStockPrice(
                quote.StockCode,
                quote.StockName,
                marketType,
                targetDate,
                quote.ClosePrice,
                providerName,
                fetchResult.SourceUrl,
                fetchResult.SourceDataDate.Value,
                batchId,
                fetchResult.FetchCompletedAt))
            .ToArray();

        var (inserted, updated) = await _repository.UpsertDailyPricesAsync(prices, cancellationToken).ConfigureAwait(false);

        var successSummary = new OfficialPriceBatchSummary(
            batchId, jobType, targetDate, providerName, marketType, fetchResult.SourceDataDate,
            startedAt, _clock.GetTaipeiNow(), prices.Length, inserted, updated, 0, 0, 0,
            OfficialPriceBatchStatus.Succeeded, null);
        await _repository.CompletePriceBatchAsync(successSummary, cancellationToken).ConfigureAwait(false);
        _logger.Info($"{providerName} {targetDate:yyyy-MM-dd}（{jobType}）官方收盤價寫入成功：新增 {inserted} 筆、更新 {updated} 筆。");
        return successSummary;
    }

    private async Task<OfficialPriceBatchSummary> RecordHolidayWithoutFetchAsync(
        OfficialPriceJobType jobType,
        DateOnly targetDate,
        string providerName,
        MarketType marketType,
        CancellationToken cancellationToken)
    {
        var startedAt = _clock.GetTaipeiNow();
        var batchId = await _repository.BeginPriceBatchAsync(jobType, targetDate, providerName, marketType, startedAt, cancellationToken).ConfigureAwait(false);
        var summary = new OfficialPriceBatchSummary(
            batchId, jobType, targetDate, providerName, marketType, null,
            startedAt, _clock.GetTaipeiNow(), 0, 0, 0, 0, 0, 0,
            OfficialPriceBatchStatus.Holiday, "週六、週日非交易日，未呼叫官方來源。");
        await _repository.CompletePriceBatchAsync(summary, cancellationToken).ConfigureAwait(false);
        return summary;
    }

    private string ResolveProviderName(MarketType marketType) => marketType switch
    {
        MarketType.Listed => _twseProvider.SourceProviderName,
        MarketType.Otc => _tpexProvider.SourceProviderName,
        MarketType.Emerging => _emergingProvider.SourceProviderName,
        _ => throw new ArgumentOutOfRangeException(nameof(marketType), marketType, "不支援的市場別。")
    };

    private static OfficialPriceBatchSummary CreateSyntheticSummary(
        OfficialPriceJobType jobType,
        DateOnly targetDate,
        string providerName,
        MarketType marketType,
        OfficialPriceBatchStatus status,
        string message)
        => new(
            $"(skip-{Guid.NewGuid():N})", jobType, targetDate, providerName, marketType, targetDate,
            DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, 0, 0, 0, 0, 0, 0, status, message);
}
