namespace YiHeLee.Domain;

/// <summary>Yi He Lee 使用者設定。固定資料來源與固定排程時間會由 Application 層再次校正。</summary>
public sealed class AppSettings
{
    public static readonly TimeOnly FixedDailyRunTime = new(13, 35);

    public string WorkbookPath { get; set; } = string.Empty;
    public string OutputWorksheetName { get; set; } = "每日五日均價策略";
    public List<string> ExcludedWorksheetNames { get; set; } = ["總表", "每日五日均價策略"];

    /// <summary>自訂程式圖示檔路徑；留空時使用內建 V1.3 圖示。支援 .ico 與常見圖片格式。</summary>
    public string AppIconPath { get; set; } = string.Empty;

    /// <summary>依專案固定規範，實際儲存時一律校正為台北時間 13:35。</summary>
    public TimeOnly DailyRunTime { get; set; } = FixedDailyRunTime;

    public int RetryIntervalMinutes { get; set; } = 10;
    public int MaximumDailyAttempts { get; set; } = 12;
    public int CrawlerShortRetryCount { get; set; } = 3;
    public int CrawlerShortRetryDelaySeconds { get; set; } = 5;
    public int ExcelShortRetryCount { get; set; } = 5;
    public int ExcelShortRetryDelaySeconds { get; set; } = 2;
    public bool StartWithWindows { get; set; } = true;
    public bool StartMinimized { get; set; } = true;
    public bool RequireBackupBeforeExcelWrite { get; set; } = true;
    public bool CreateOutputWorksheetIfMissing { get; set; } = true;
    public bool ShowExcelSafetyPrompt { get; set; } = true;

    /// <summary>找不到已開啟的指定活頁簿時，自動用 Excel 開啟該檔案，不再要求使用者手動開啟。</summary>
    public bool AutoOpenWorkbookIfClosed { get; set; } = true;

    /// <summary>主視窗「操作」頁籤按鈕、系統匣（右下角）選單「歷史收盤價」項目是否顯示。依使用者要求預設顯示。</summary>
    public bool ShowHistoricalPriceButton { get; set; } = true;

    /// <summary>主視窗「操作」頁籤是否顯示執行中的文字狀態（顯示目前正在擷取／計算哪個步驟）。
    /// 依使用者要求預設顯示；設為 false 時只呈現 0～100% 進度條。與 ShowHistoricalPriceButton 一樣，
    /// 故意不放進設定頁籤 UI，只是 config 旗標。</summary>
    public bool ShowStatusText { get; set; } = true;

    /// <summary>設定頁是否顯示「資料來源網址」頁籤（鉅亨網來源清單）。預設隱藏，避免使用者誤改來源設定；
    /// 與 ShowHistoricalPriceButton 一樣，故意不放進設定頁籤 UI，只是 config 旗標，儲存設定時原樣保留。</summary>
    public bool ShowSourceSettings { get; set; } = false;

    /// <summary>是否啟用鉅亨網址均價交叉比對；關閉時仍保留固定來源與正式 TWSE／TPEx 均價計算。</summary>
    public bool EnableCnyesMovingAverageComparison { get; set; } = false;

    /// <summary>每日 13:35 自動排程是否啟用。設為 false 時排程不執行，使用者仍可手動「立即執行」。
    /// 2026-07-11 新增，預設啟用以保持既有行為。</summary>
    public bool EnableDailySchedule { get; set; } = true;

    /// <summary>
    /// 持股列若「股名」儲存格套用這些 RGB 填滿色，就視為人工標記的不判斷資料。
    /// 預設 #92D050 為使用者提供範例中的綠色。
    /// </summary>
    public List<string> ExcludedHoldingFillColors { get; set; } = ["#92D050"];

    /// <summary>持股列出現以下文字時略過；用於「不判斷、已出場」等人工註記。</summary>
    public List<string> ExcludedHoldingTextMarkers { get; set; } = ["不判斷", "不用判斷", "忽略", "已出場", "暫停判斷"];

    public List<SourceDefinitionSetting> Sources { get; set; } = SourceDefinitionSetting.CreateDefaults();

    /// <summary>官方（TWSE／TPEx）每日收盤價與均線計算相關設定。</summary>
    public OfficialMarketDataSettings OfficialMarketData { get; set; } = new();

    /// <summary>歷史收盤價手動回補（歷史收盤價查詢畫面「立即回補」）相關設定。</summary>
    public StockHistoryImportOptions StockHistoryImport { get; set; } = new();

    public static AppSettings CreateDefault() => new();
}

/// <summary>
/// 歷史收盤價手動回補設定：可回補的交易日數、並行上限、逾時與重試皆可設定，
/// 但「當日日期驗證」「不得回抓前一交易日頂替」等規則不受本設定影響。
/// </summary>
public sealed class StockHistoryImportOptions
{
    /// <summary>預設回補的有效交易日數。</summary>
    public int DefaultTradingDays { get; set; } = 5;

    /// <summary>畫面可選擇的最大有效交易日數（自訂上限）。</summary>
    public int MaxSelectableTradingDays { get; set; } = 250;

    /// <summary>同時並行的抓取工作數上限；預設4，規範上限為8。</summary>
    public int MaxConcurrency { get; set; } = 4;

    /// <summary>單次 HTTP 請求逾時秒數。</summary>
    public int RequestTimeoutSeconds { get; set; } = 30;

    /// <summary>暫時性錯誤（HTTP 408／429／5xx、網路中斷、逾時）最大重試次數。</summary>
    public int MaxRetryCount { get; set; } = 3;

    /// <summary>指數退避的基礎秒數（第1次重試等待此秒數，之後倍增，例如2、4、8秒）。</summary>
    public int RetryBaseDelaySeconds { get; set; } = 2;

    /// <summary>依規範上限（不得超過8）與最小值1校正並行數。</summary>
    public int ClampedMaxConcurrency() => Math.Clamp(MaxConcurrency, 1, 8);

    /// <summary>依 MaxSelectableTradingDays 與最小值1校正使用者實際選擇的交易日數。</summary>
    public int ClampTradingDays(int requested) => Math.Clamp(requested, 1, Math.Max(1, MaxSelectableTradingDays));
}

/// <summary>
/// 官方每日收盤價來源與均線計算設定。網址、逾時、重試與回看範圍可設定，
/// 但「當日日期驗證」與「不得回抓前一交易日」規則不得由設定關閉。
/// </summary>
public sealed class OfficialMarketDataSettings
{
    /// <summary>TWSE 官方每日收盤行情（可指定日期）端點，{0} 置換為 yyyyMMdd。</summary>
    public string TwseDailyCloseUrlTemplate { get; set; } =
        "https://www.twse.com.tw/exchangeReport/MI_INDEX?response=json&date={0}&type=ALLBUT0999";

    /// <summary>TPEx 官方每日收盤行情（可指定日期）端點，{0} 置換為民國年/月/日。</summary>
    public string TpexDailyCloseUrlTemplate { get; set; } =
        "https://www.tpex.org.tw/www/zh-tw/afterTrading/otc?date={0}&type=EW&response=json";

    /// <summary>
    /// TPEx 官方興櫃股票當日行情端點（OpenAPI，無日期參數，僅回報呼叫當下的即時快照）。
    /// 用於每日排程寫入 targetDate 當日價格；歷史回補改用 EmergingHistoricalUrlTemplate。
    /// </summary>
    public string EmergingDailyCloseUrl { get; set; } =
        "https://www.tpex.org.tw/openapi/v1/tpex_esb_latest_statistics";

    /// <summary>
    /// TPEx 官方興櫃個股歷史行情端點，{0} 置換為民國年月（yyy/MM），{1} 置換為股票代碼。
    /// 官方只提供個股月份查詢，因此本系統只針對 Excel 持股中的興櫃股票補缺漏日期。
    /// </summary>
    public string EmergingHistoricalUrlTemplate { get; set; } =
        "https://www.tpex.org.tw/www/zh-tw/emerging/historical?date={0}&code={1}&type=Monthly&response=json";

    public int HttpTimeoutSeconds { get; set; } = 30;
    public int HttpShortRetryCount { get; set; } = 3;
    public int HttpShortRetryDelaySeconds { get; set; } = 5;

    /// <summary>MA120 所需的最少有效交易日數。</summary>
    public int RequiredTradingDaysForMa120 { get; set; } = 120;

    /// <summary>歷史回補最大回看日曆日數，避免無限期往前抓取。</summary>
    public int MaxBackfillLookbackCalendarDays { get; set; } = 280;

    /// <summary>歷史回補逐日呼叫官方來源之間的節流間隔，避免大量平行轟炸官方網站。</summary>
    public int BackfillThrottleMillisecondsBetweenRequests { get; set; } = 300;
}

/// <summary>可設定 N 個網址；每一個 ProviderKey 對應一個獨立爬蟲實作。</summary>
public sealed class SourceDefinitionSetting
{
    public string SourceKey { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
    public string Url { get; set; } = string.Empty;
    public IndicatorType IndicatorType { get; set; }
    public string ProviderKey { get; set; } = "CnyesTechnicalAlignment";
    public bool Enabled { get; set; } = true;
    public bool Required { get; set; }

    public SourceDefinition ToDomain()
    {
        if (!Uri.TryCreate(Url, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps)
        {
            throw new InvalidOperationException($"來源網址格式不正確：{Url}");
        }

        return new SourceDefinition(SourceKey, DisplayName, uri, IndicatorType, ProviderKey, Enabled, Required);
    }

    public SourceDefinitionSetting Clone() => new()
    {
        SourceKey = SourceKey,
        DisplayName = DisplayName,
        Url = Url,
        IndicatorType = IndicatorType,
        ProviderKey = ProviderKey,
        Enabled = Enabled,
        Required = Required
    };

    public static List<SourceDefinitionSetting> CreateDefaults() =>
    [
        new()
        {
            SourceKey = "CNYES_BULLISH_ALIGNMENT",
            DisplayName = "鉅亨網－股價多頭排列",
            Url = "https://www.cnyes.com/twstock/a_technical4.aspx",
            IndicatorType = IndicatorType.BullishAlignment,
            ProviderKey = "CnyesTechnicalAlignment",
            Enabled = true,
            Required = true
        },
        new()
        {
            SourceKey = "CNYES_BEARISH_ALIGNMENT",
            DisplayName = "鉅亨網－股價空頭排列",
            Url = "https://www.cnyes.com/twstock/a_technical5.aspx",
            IndicatorType = IndicatorType.BearishAlignment,
            ProviderKey = "CnyesTechnicalAlignment",
            Enabled = true,
            Required = true
        }
    ];
}
