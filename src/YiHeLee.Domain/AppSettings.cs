namespace YiHeLee.Domain;

/// <summary>Yi He Lee 使用者設定。固定資料來源與固定排程時間會由 Application 層再次校正。</summary>
public sealed class AppSettings
{
    public static readonly TimeOnly FixedDailyRunTime = new(13, 35);

    public string WorkbookPath { get; set; } = string.Empty;
    public string OutputWorksheetName { get; set; } = "每日五日均價策略";
    public List<string> ExcludedWorksheetNames { get; set; } = ["總表", "每日五日均價策略"];

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

    public static AppSettings CreateDefault() => new();
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
        "https://www.tpex.org.tw/web/stock/aftertrading/daily_close_quotes/stk_quote_result.php?l=zh-tw&d={0}&s=0,asc,0";

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
